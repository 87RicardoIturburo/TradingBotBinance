using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using TradingBot.Core.Entities;
using TradingBot.Core.Events;
using TradingBot.Core.Interfaces.Repositories;
using TradingBot.Core.Interfaces.Services;
using TradingBot.Core.Interfaces.Trading;
using TradingBot.Core.ValueObjects;

namespace TradingBot.Application.Strategies;

/// <summary>
/// Motor de estrategias. Orquesta el flujo completo como <see cref="BackgroundService"/>:
/// <c>MarketTick → ITradingStrategy → IRuleEngine → IRiskManager → IOrderService</c>.
/// <para>
/// Cada estrategia activa se ejecuta en su propio loop asíncrono consumiendo
/// el <see cref="IAsyncEnumerable{T}"/> de ticks del <see cref="IMarketDataService"/>.
/// </para>
/// </summary>
internal sealed class StrategyEngine : BackgroundService, IStrategyEngine
{
    private readonly IServiceScopeFactory                             _scopeFactory;
    private readonly IMarketDataService                               _marketDataService;
    private readonly ILogger<StrategyEngine>                          _logger;
    private readonly ConcurrentDictionary<Guid, StrategyRunnerState>  _runners = new();

    private volatile bool _isPaused;

    public bool IsRunning => !_isPaused && _runners.Values.Any(r => r.IsProcessing);

    public StrategyEngine(
        IServiceScopeFactory scopeFactory,
        IMarketDataService   marketDataService,
        ILogger<StrategyEngine> logger)
    {
        _scopeFactory      = scopeFactory;
        _marketDataService = marketDataService;
        _logger            = logger;
    }

    // ── BackgroundService ──────────────────────────────────────────────────

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("StrategyEngine arrancando…");

        await LoadAndStartActiveStrategiesAsync(stoppingToken);

        _logger.LogInformation(
            "StrategyEngine iniciado con {Count} estrategias activas", _runners.Count);

        // Mantener vivo hasta que se detenga el host
        try
        {
            await Task.Delay(Timeout.Infinite, stoppingToken);
        }
        catch (OperationCanceledException)
        {
            // Shutdown normal
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("StrategyEngine deteniéndose…");

        foreach (var runner in _runners.Values)
            runner.Cancel();

        // Esperar a que todos los loops terminen
        var tasks = _runners.Values
            .Where(r => r.ProcessingTask is not null)
            .Select(r => r.ProcessingTask!);

        await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);

        _runners.Clear();
        _logger.LogInformation("StrategyEngine detenido");

        await base.StopAsync(cancellationToken);
    }

    // ── IStrategyEngine ────────────────────────────────────────────────────

    Task IStrategyEngine.StartAsync(CancellationToken cancellationToken) =>
        StartAsync(cancellationToken);

    Task IStrategyEngine.StopAsync(CancellationToken cancellationToken) =>
        StopAsync(cancellationToken);

    public Task PauseAsync(CancellationToken cancellationToken = default)
    {
        _isPaused = true;
        _logger.LogInformation("StrategyEngine pausado — los WebSockets siguen conectados");
        return Task.CompletedTask;
    }

    public Task ResumeAsync(CancellationToken cancellationToken = default)
    {
        _isPaused = false;
        _logger.LogInformation("StrategyEngine reanudado");
        return Task.CompletedTask;
    }

    public async Task ReloadStrategyAsync(Guid strategyId, CancellationToken cancellationToken = default)
    {
        using var scope = _scopeFactory.CreateScope();
        var strategyRepo = scope.ServiceProvider.GetRequiredService<IStrategyRepository>();
        var config = await strategyRepo.GetWithRulesAsync(strategyId, cancellationToken);

        if (config is null || !config.IsActive)
        {
            // Si se desactivó, detener el runner
            if (_runners.TryRemove(strategyId, out var removed))
            {
                removed.Cancel();
                _logger.LogInformation("Runner para estrategia {Id} detenido (desactivada)", strategyId);
            }
            return;
        }

        if (_runners.TryGetValue(strategyId, out var existing))
        {
            // Hot-reload: recargar config en la instancia existente
            await existing.Strategy.ReloadConfigAsync(config, cancellationToken);
            _logger.LogInformation("Hot-reload completado para estrategia '{Name}' ({Id})", config.Name, strategyId);
        }
        else
        {
            // Estrategia nueva activada → arrancar runner
            await StartStrategyRunnerAsync(config, cancellationToken);
        }
    }

    public Task<IReadOnlyDictionary<Guid, StrategyEngineStatus>> GetStatusAsync(
        CancellationToken cancellationToken = default)
    {
        var statuses = _runners.ToDictionary(
            kv => kv.Key,
            kv => kv.Value.ToStatus());

        return Task.FromResult<IReadOnlyDictionary<Guid, StrategyEngineStatus>>(statuses);
    }

    // ── Carga inicial ──────────────────────────────────────────────────────

    private async Task LoadAndStartActiveStrategiesAsync(CancellationToken cancellationToken)
    {
        using var scope = _scopeFactory.CreateScope();
        var strategyRepo = scope.ServiceProvider.GetRequiredService<IStrategyRepository>();
        var strategies   = await strategyRepo.GetActiveStrategiesAsync(cancellationToken);

        foreach (var strategy in strategies)
        {
            try
            {
                await StartStrategyRunnerAsync(strategy, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error al arrancar runner para '{Name}' ({Id})", strategy.Name, strategy.Id);
            }
        }
    }

    private async Task StartStrategyRunnerAsync(TradingStrategy config, CancellationToken cancellationToken)
    {
        // Crear instancia de ITradingStrategy con su propio scope
        using var scope    = _scopeFactory.CreateScope();
        var tradingStrategy = scope.ServiceProvider.GetRequiredService<ITradingStrategy>();
        await tradingStrategy.InitializeAsync(config, cancellationToken);

        // Precalentar indicadores con datos históricos
        await WarmUpIndicatorsAsync(tradingStrategy, config, cancellationToken);

        // Suscribir al WebSocket
        await _marketDataService.SubscribeAsync(config.Symbol, cancellationToken);

        // Crear y arrancar el runner
        var cts    = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var runner = new StrategyRunnerState(config.Id, config.Name, config.Symbol, tradingStrategy, cts);
        runner.ProcessingTask = Task.Run(() => ProcessTicksLoopAsync(runner), cts.Token);

        _runners[config.Id] = runner;

        _logger.LogInformation(
            "Runner arrancado para '{Name}' ({Id}) en {Symbol}",
            config.Name, config.Id, config.Symbol.Value);
    }

    private async Task WarmUpIndicatorsAsync(
        ITradingStrategy tradingStrategy,
        TradingStrategy  config,
        CancellationToken cancellationToken)
    {
        var maxPeriod = config.Indicators
            .Select(i => (int)i.GetParameter("period", 14))
            .DefaultIfEmpty(0)
            .Max();

        if (maxPeriod <= 0) return;

        // Pedir datos históricos suficientes para calentar los indicadores
        var closesResult = await _marketDataService.GetHistoricalClosesAsync(
            config.Symbol, maxPeriod + 10, cancellationToken);

        if (closesResult.IsFailure)
        {
            _logger.LogWarning(
                "No se pudieron obtener datos históricos para {Symbol}: {Error}",
                config.Symbol.Value, closesResult.Error.Message);
            return;
        }

        // Alimentar cada cierre histórico como tick sintético para calentar indicadores
        foreach (var close in closesResult.Value)
        {
            var priceResult = Price.Create(close);
            if (priceResult.IsFailure) continue;

            var syntheticTick = new MarketTickReceivedEvent(
                config.Symbol,
                priceResult.Value, priceResult.Value, priceResult.Value,
                0m, DateTimeOffset.UtcNow);

            await tradingStrategy.ProcessTickAsync(syntheticTick, cancellationToken);
        }

        _logger.LogDebug(
            "Warm-up completado para '{Name}': {Count} cierres históricos procesados",
            config.Name, closesResult.Value.Count);
    }

    // ── Loop principal por estrategia ──────────────────────────────────────

    private async Task ProcessTicksLoopAsync(StrategyRunnerState runner)
    {
        var token = runner.CancellationTokenSource.Token;
        const int maxConsecutiveErrors = 10;
        var consecutiveErrors = 0;

        _logger.LogDebug("Tick loop iniciado para '{Name}' ({Id})", runner.StrategyName, runner.StrategyId);

        try
        {
            await foreach (var tick in _marketDataService.GetTickStreamAsync(runner.Symbol, token))
            {
                if (token.IsCancellationRequested) break;

                if (_isPaused)
                {
                    runner.IsProcessing = false;
                    continue;
                }

                runner.IsProcessing = true;

                try
                {
                    await ProcessSingleTickAsync(runner, tick, token);
                    consecutiveErrors = 0;
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    consecutiveErrors++;
                    _logger.LogError(ex,
                        "Error procesando tick para '{Name}' ({Id}). Consecutivos: {Count}/{Max}",
                        runner.StrategyName, runner.StrategyId,
                        consecutiveErrors, maxConsecutiveErrors);

                    if (consecutiveErrors >= maxConsecutiveErrors)
                    {
                        _logger.LogCritical(
                            "Estrategia '{Name}' ({Id}) marcada como Error tras {Max} fallos consecutivos",
                            runner.StrategyName, runner.StrategyId, maxConsecutiveErrors);

                        await MarkStrategyAsErrorAsync(runner.StrategyId, token);
                        break;
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Shutdown normal
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Tick loop terminado inesperadamente para '{Name}' ({Id})",
                runner.StrategyName, runner.StrategyId);
        }
        finally
        {
            runner.IsProcessing = false;
            _logger.LogDebug("Tick loop finalizado para '{Name}' ({Id})", runner.StrategyName, runner.StrategyId);
        }
    }

    private async Task ProcessSingleTickAsync(
        StrategyRunnerState runner,
        MarketTickReceivedEvent tick,
        CancellationToken cancellationToken)
    {
        runner.TicksProcessed++;
        runner.LastTickAt = tick.Timestamp;

        // 1. ITradingStrategy → genera señal si los indicadores lo indican
        var signalResult = await runner.Strategy.ProcessTickAsync(tick, cancellationToken);
        if (signalResult.IsFailure)
        {
            _logger.LogWarning(
                "Error en ProcessTick para '{Name}': {Error}",
                runner.StrategyName, signalResult.Error.Message);
            return;
        }

        // 2. Usar un scope nuevo para los servicios scoped (repos, UoW)
        using var scope       = _scopeFactory.CreateScope();
        var ruleEngine        = scope.ServiceProvider.GetRequiredService<IRuleEngine>();
        var orderService      = scope.ServiceProvider.GetRequiredService<IOrderService>();
        var strategyRepo      = scope.ServiceProvider.GetRequiredService<IStrategyRepository>();
        var positionRepo      = scope.ServiceProvider.GetRequiredService<IPositionRepository>();

        // Cargar la config fresca para tener reglas actualizadas
        var strategy = await strategyRepo.GetWithRulesAsync(runner.StrategyId, cancellationToken);
        if (strategy is null || !strategy.IsActive) return;

        // 3. Si hay señal → evaluar reglas de entrada
        if (signalResult.Value is { } signal)
        {
            runner.SignalsGenerated++;

            var orderResult = await ruleEngine.EvaluateAsync(strategy, signal, cancellationToken);
            if (orderResult.IsSuccess && orderResult.Value is { } order)
            {
                var placeResult = await orderService.PlaceOrderAsync(order, cancellationToken);
                if (placeResult.IsSuccess)
                {
                    runner.OrdersPlaced++;
                    _logger.LogInformation(
                        "Orden colocada por '{Name}': {Side} {Qty} {Symbol}",
                        runner.StrategyName, order.Side, order.Quantity.Value, order.Symbol.Value);
                }
                else
                {
                    _logger.LogWarning(
                        "Orden rechazada para '{Name}': {Error}",
                        runner.StrategyName, placeResult.Error.Message);
                }
            }
        }

        // 4. Evaluar reglas de salida para posiciones abiertas
        var openPositions = await positionRepo.GetOpenByStrategyIdAsync(
            runner.StrategyId, cancellationToken);

        foreach (var position in openPositions)
        {
            position.UpdatePrice(tick.LastPrice);

            var exitResult = await ruleEngine.EvaluateExitRulesAsync(
                strategy, position, tick.LastPrice, cancellationToken);

            if (exitResult.IsSuccess && exitResult.Value is { } exitOrder)
            {
                var placeResult = await orderService.PlaceOrderAsync(exitOrder, cancellationToken);
                if (placeResult.IsSuccess)
                {
                    runner.OrdersPlaced++;
                    _logger.LogInformation(
                        "Orden de salida colocada por '{Name}': {Side} {Symbol} (posición {PosId})",
                        runner.StrategyName, exitOrder.Side, exitOrder.Symbol.Value, position.Id);
                }
            }
        }
    }

    private async Task MarkStrategyAsErrorAsync(Guid strategyId, CancellationToken cancellationToken)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var repo     = scope.ServiceProvider.GetRequiredService<IStrategyRepository>();
            var unitOfWork = scope.ServiceProvider.GetRequiredService<Core.Interfaces.IUnitOfWork>();
            var strategy = await repo.GetByIdAsync(strategyId, cancellationToken);

            if (strategy is null) return;

            strategy.MarkAsError();
            await repo.UpdateAsync(strategy, cancellationToken);
            await unitOfWork.SaveChangesAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "No se pudo marcar la estrategia {Id} como Error", strategyId);
        }
    }

    // ── Estado interno por estrategia ──────────────────────────────────────

    private sealed class StrategyRunnerState
    {
        public Guid                       StrategyId             { get; }
        public string                     StrategyName           { get; }
        public Symbol                     Symbol                 { get; }
        public ITradingStrategy           Strategy               { get; }
        public CancellationTokenSource    CancellationTokenSource { get; }
        public Task?                      ProcessingTask         { get; set; }
        public bool                       IsProcessing           { get; set; }
        public DateTimeOffset             LastTickAt             { get; set; }
        public int                        TicksProcessed         { get; set; }
        public int                        SignalsGenerated       { get; set; }
        public int                        OrdersPlaced           { get; set; }

        public StrategyRunnerState(
            Guid id, string name, Symbol symbol,
            ITradingStrategy strategy, CancellationTokenSource cts)
        {
            StrategyId              = id;
            StrategyName            = name;
            Symbol                  = symbol;
            Strategy                = strategy;
            CancellationTokenSource = cts;
        }

        public void Cancel() => CancellationTokenSource.Cancel();

        public StrategyEngineStatus ToStatus() => new(
            StrategyId, StrategyName, Symbol,
            IsProcessing, LastTickAt,
            TicksProcessed, SignalsGenerated, OrdersPlaced);
    }
}
