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

    public bool IsRunning => !_isPaused && !_runners.IsEmpty;

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

        // Watchdog: verificar periódicamente que todas las estrategias reciben ticks
        _ = RunTickWatchdogAsync(stoppingToken);

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

        foreach (var runner in _runners.Values)
            runner.Dispose();

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
                removed.Dispose();
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

        // Evaluar posiciones abiertas inmediatamente al arrancar
        await EvaluateOpenPositionsOnStartupAsync(config, cancellationToken);

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

    // ── Evaluación de posiciones huérfanas al reiniciar ──────────────────

    /// <summary>
    /// Al reiniciar, evalúa stop-loss/take-profit de posiciones abiertas de la estrategia
    /// con el precio actual de mercado. Protege contra movimientos de precio durante el downtime.
    /// </summary>
    private async Task EvaluateOpenPositionsOnStartupAsync(
        TradingStrategy config,
        CancellationToken cancellationToken)
    {
        try
        {
            using var scope     = _scopeFactory.CreateScope();
            var positionRepo    = scope.ServiceProvider.GetRequiredService<IPositionRepository>();
            var ruleEngine      = scope.ServiceProvider.GetRequiredService<IRuleEngine>();
            var orderService    = scope.ServiceProvider.GetRequiredService<IOrderService>();
            var notifier        = scope.ServiceProvider.GetService<ITradingNotifier>();

            var openPositions = await positionRepo.GetOpenByStrategyIdAsync(config.Id, cancellationToken);
            if (openPositions.Count == 0) return;

            _logger.LogInformation(
                "Evaluando {Count} posiciones abiertas de '{Name}' al reiniciar",
                openPositions.Count, config.Name);

            // Obtener precio actual para evaluar stop-loss/take-profit
            var priceResult = await _marketDataService.GetCurrentPriceAsync(config.Symbol, cancellationToken);
            if (priceResult.IsFailure)
            {
                _logger.LogWarning(
                    "No se pudo obtener precio actual para evaluar posiciones de '{Name}': {Error}",
                    config.Name, priceResult.Error.Message);
                return;
            }

            var currentPrice = priceResult.Value;

            foreach (var position in openPositions)
            {
                position.UpdatePrice(currentPrice);

                var exitResult = await ruleEngine.EvaluateExitRulesAsync(
                    config, position, currentPrice, cancellationToken);

                if (exitResult.IsSuccess && exitResult.Value is { } exitOrder)
                {
                    var placeResult = await orderService.PlaceOrderAsync(exitOrder, cancellationToken);
                    if (placeResult.IsSuccess)
                    {
                        _logger.LogWarning(
                            "🚨 Posición {PosId} cerrada al reiniciar por stop-loss/take-profit: {Side} {Symbol}",
                            position.Id, exitOrder.Side, exitOrder.Symbol.Value);

                        if (notifier is not null)
                        {
                            await notifier.NotifyOrderExecutedAsync(
                                new OrderPlacedEvent(
                                    exitOrder.Id, exitOrder.StrategyId, exitOrder.Symbol,
                                    exitOrder.Side, exitOrder.Type, exitOrder.Quantity,
                                    exitOrder.LimitPrice, exitOrder.IsPaperTrade),
                                cancellationToken);

                            await notifier.NotifyAlertAsync(
                                $"Posición de '{config.Name}' cerrada automáticamente al reiniciar (SL/TP durante downtime)",
                                cancellationToken);
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error evaluando posiciones abiertas al reiniciar para '{Name}'", config.Name);
        }
    }

    // ── Tick Watchdog ──────────────────────────────────────────────────────

    /// <summary>
    /// Verifica periódicamente que las estrategias activas reciban ticks.
    /// Si una estrategia no recibe ticks por más de 5 minutos, genera una alerta.
    /// </summary>
    private async Task RunTickWatchdogAsync(CancellationToken cancellationToken)
    {
        const int checkIntervalSeconds = 60;
        const int maxSilenceMinutes    = 5;

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(checkIntervalSeconds), cancellationToken);

                var now = DateTimeOffset.UtcNow;

                foreach (var runner in _runners.Values)
                {
                    if (runner.LastTickAt == default) continue;

                    var silence = now - runner.LastTickAt;
                    if (silence <= TimeSpan.FromMinutes(maxSilenceMinutes)) continue;

                    _logger.LogWarning(
                        "⚠ Watchdog: estrategia '{Name}' ({Id}) sin ticks por {Minutes:F1} minutos",
                        runner.StrategyName, runner.StrategyId, silence.TotalMinutes);

                    try
                    {
                        using var scope  = _scopeFactory.CreateScope();
                        var notifier     = scope.ServiceProvider.GetService<ITradingNotifier>();
                        if (notifier is not null)
                            await notifier.NotifyAlertAsync(
                                $"⚠ Estrategia '{runner.StrategyName}' sin recibir ticks por {silence.TotalMinutes:F0} minutos. Verifique la conexión WebSocket.",
                                cancellationToken);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Error enviando alerta de watchdog");
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Shutdown normal
        }
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
        var notifier          = scope.ServiceProvider.GetService<ITradingNotifier>();

        // Notificar tick al frontend vía SignalR
        if (notifier is not null)
            await notifier.NotifyMarketTickAsync(tick, cancellationToken);

        // Cargar la config fresca para tener reglas actualizadas
        var strategy = await strategyRepo.GetWithRulesAsync(runner.StrategyId, cancellationToken);
        if (strategy is null || !strategy.IsActive) return;

        // 3. Si hay señal → evaluar reglas de entrada
        if (signalResult.Value is { } signal)
        {
            runner.SignalsGenerated++;

            if (notifier is not null)
                await notifier.NotifySignalGeneratedAsync(signal, cancellationToken);

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

                    if (notifier is not null)
                        await notifier.NotifyOrderExecutedAsync(
                            new OrderPlacedEvent(
                                order.Id, order.StrategyId, order.Symbol,
                                order.Side, order.Type, order.Quantity,
                                order.LimitPrice, order.IsPaperTrade),
                            cancellationToken);
                }
                else
                {
                    _logger.LogWarning(
                        "Orden rechazada para '{Name}': {Error}",
                        runner.StrategyName, placeResult.Error.Message);

                    if (notifier is not null)
                        await notifier.NotifyAlertAsync(
                            $"Orden rechazada para '{runner.StrategyName}': {placeResult.Error.Message}",
                            cancellationToken);
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

                    if (notifier is not null)
                        await notifier.NotifyOrderExecutedAsync(
                            new OrderPlacedEvent(
                                exitOrder.Id, exitOrder.StrategyId, exitOrder.Symbol,
                                exitOrder.Side, exitOrder.Type, exitOrder.Quantity,
                                exitOrder.LimitPrice, exitOrder.IsPaperTrade),
                            cancellationToken);
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

    private sealed class StrategyRunnerState : IDisposable
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

        public void Cancel()
        {
            CancellationTokenSource.Cancel();
        }

        public void Dispose()
        {
            CancellationTokenSource.Dispose();
        }

        public StrategyEngineStatus ToStatus() => new(
            StrategyId, StrategyName, Symbol,
            IsProcessing, LastTickAt,
            TicksProcessed, SignalsGenerated, OrdersPlaced);
    }
}
