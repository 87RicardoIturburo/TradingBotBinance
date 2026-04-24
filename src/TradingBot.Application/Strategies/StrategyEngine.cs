using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TradingBot.Application.Diagnostics;
using TradingBot.Application.Explainer;
using TradingBot.Application.RiskManagement;
using TradingBot.Application.Strategies.Indicators;
using TradingBot.Core.Entities;
using TradingBot.Core.Enums;
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
    private readonly IGlobalCircuitBreaker                            _circuitBreaker;
    private readonly IIndicatorStateStore                             _indicatorStateStore;
    private readonly StrategyResolver                                 _strategyResolver;
    private readonly GlobalRiskSettings                                _globalRisk;
    private readonly TradingMetrics                                   _metrics;
    private readonly IDefaultPoolTemplateFactory                       _poolTemplateFactory;
    private readonly ILogger<StrategyEngine>                          _logger;
    private readonly ConcurrentDictionary<Guid, StrategyRunnerState>  _runners = new();
    /// <summary>Semáforo por estrategia para serializar el procesamiento de ticks y evitar órdenes duplicadas.</summary>
    private readonly ConcurrentDictionary<Guid, SemaphoreSlim>       _strategyLocks = new();

    private volatile bool _isPaused;

    public bool IsRunning => !_isPaused && !_runners.IsEmpty;

    public StrategyEngine(
        IServiceScopeFactory   scopeFactory,
        IMarketDataService     marketDataService,
        IGlobalCircuitBreaker  circuitBreaker,
        IIndicatorStateStore   indicatorStateStore,
        StrategyResolver       strategyResolver,
        IOptions<GlobalRiskSettings> globalRiskOptions,
        TradingMetrics         metrics,
        IDefaultPoolTemplateFactory poolTemplateFactory,
        ILogger<StrategyEngine> logger)
    {
        _scopeFactory        = scopeFactory;
        _marketDataService   = marketDataService;
        _circuitBreaker      = circuitBreaker;
        _indicatorStateStore = indicatorStateStore;
        _strategyResolver    = strategyResolver;
        _globalRisk          = globalRiskOptions.Value;
        _metrics             = metrics;
        _poolTemplateFactory = poolTemplateFactory;
        _logger              = logger;
    }

    // ── BackgroundService ──────────────────────────────────────────────────

    private const int MaxStartupRetries = 10;
    private static readonly TimeSpan InitialRetryDelay = TimeSpan.FromSeconds(5);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("StrategyEngine arrancando…");

        await InitializeRiskBudgetAsync(stoppingToken);

        await LoadWithRetryAsync(stoppingToken);

        _logger.LogInformation(
            "StrategyEngine iniciado con {Count} estrategias activas", _runners.Count);

        _ = RunTickWatchdogAsync(stoppingToken);

        _ = RunDrawdownCheckerAsync(stoppingToken);

        try
        {
            await Task.Delay(Timeout.Infinite, stoppingToken);
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task InitializeRiskBudgetAsync(CancellationToken stoppingToken)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var riskBudget = scope.ServiceProvider.GetRequiredService<IRiskBudget>();
            await riskBudget.RefreshAsync(stoppingToken);
            _logger.LogInformation("RiskBudget inicializado: nivel={Level}", riskBudget.CurrentLevel);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "No se pudo inicializar el RiskBudget al arrancar. Se usará nivel Normal por defecto.");
        }
    }

    private async Task LoadWithRetryAsync(CancellationToken stoppingToken)
    {
        var delay = InitialRetryDelay;

        for (var attempt = 1; attempt <= MaxStartupRetries; attempt++)
        {
            try
            {
                await LoadAndStartActiveStrategiesAsync(stoppingToken);
                return;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "No se pudo cargar estrategias activas (intento {Attempt}/{Max}). Reintentando en {Delay}s…",
                    attempt, MaxStartupRetries, delay.TotalSeconds);

                if (attempt == MaxStartupRetries)
                {
                    _logger.LogError(
                        "Se agotaron los reintentos ({Max}) para cargar estrategias. " +
                        "El engine arrancará sin estrategias activas y esperará comandos manuales.",
                        MaxStartupRetries);
                    return;
                }

                await Task.Delay(delay, stoppingToken);
                delay = TimeSpan.FromSeconds(Math.Min(delay.TotalSeconds * 2, 60));
            }
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

        // Dispose semáforos
        foreach (var semaphore in _strategyLocks.Values)
            semaphore.Dispose();
        _strategyLocks.Clear();

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
                _strategyResolver.Remove(strategyId);
                if (_strategyLocks.TryRemove(strategyId, out var removedLock))
                    removedLock.Dispose();
                _logger.LogInformation("Runner para estrategia {Id} detenido (desactivada)", strategyId);
            }
            return;
        }

        if (_runners.TryGetValue(strategyId, out var existing))
        {
            // Hot-reload: recargar config en TODAS las instancias y re-warm-up
            await _strategyResolver.ReloadAllAsync(strategyId, config, cancellationToken);
            existing.CachedConfig = config;

            var allStrategies = _strategyResolver.GetAllStrategies(strategyId);
            foreach (var s in allStrategies)
                await WarmUpIndicatorsAsync(s, config, cancellationToken);

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

    // ── Pool v2: métodos de gestión de runners de pool ─────────────────────

    public async Task<Core.Common.Result<Guid, Core.Common.DomainError>> StartPoolRunnerAsync(
        string symbol, Guid templateId, string timeframe, string tradingMode,
        CancellationToken ct = default)
    {
        var existing = _runners.Values.FirstOrDefault(
            r => r.IsPoolRunner && r.Symbol.Value.Equals(symbol, StringComparison.OrdinalIgnoreCase));
        if (existing is not null)
            return Core.Common.Result<Guid, Core.Common.DomainError>.Success(existing.StrategyId);

        using var scope = _scopeFactory.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IStrategyRepository>();
        var unitOfWork = scope.ServiceProvider.GetRequiredService<Core.Interfaces.IUnitOfWork>();

        // Anti-duplicación: buscar estrategia Pool existente en DB para este symbol
        var existingPool = await repo.GetPoolStrategyBySymbolAsync(symbol, ct);
        if (existingPool is not null)
        {
            if (!existingPool.IsActive)
            {
                existingPool.Activate();
                await repo.UpdateAsync(existingPool, ct);
                await unitOfWork.SaveChangesAsync(ct);
            }

            await StartStrategyRunnerAsync(existingPool, ct);

            if (_runners.TryGetValue(existingPool.Id, out var reusedRunner))
            {
                reusedRunner.IsPoolRunner = true;
                reusedRunner.AllowNewEntries = false;
                reusedRunner.BlockReason = "Cooldown";
            }

            _logger.LogInformation(
                "Pool runner reutilizado para {Symbol} (Id={Id})", symbol, existingPool.Id);
            return Core.Common.Result<Guid, Core.Common.DomainError>.Success(existingPool.Id);
        }

        var template = await repo.GetWithRulesAsync(templateId, ct);

        if (!Enum.TryParse<TradingMode>(tradingMode, true, out var mode))
            mode = TradingMode.PaperTrading;
        if (!Enum.TryParse<CandleInterval>(timeframe, true, out var interval))
            interval = template?.Timeframe ?? CandleInterval.FifteenMinutes;

        TradingStrategy poolConfig;

        if (template is not null)
        {
            var symbolVo = Symbol.Create(symbol);
            if (symbolVo.IsFailure)
                return Core.Common.Result<Guid, Core.Common.DomainError>.Failure(symbolVo.Error);

            var createResult = TradingStrategy.Create(
                $"Pool-{symbol}", symbolVo.Value, mode,
                template.RiskConfig, $"AutoPilot v2 pool runner para {symbol}",
                interval, template.ConfirmationTimeframe,
                origin: StrategyOrigin.Pool);

            if (createResult.IsFailure)
                return Core.Common.Result<Guid, Core.Common.DomainError>.Failure(createResult.Error);

            poolConfig = createResult.Value;

            foreach (var ind in template.Indicators)
                poolConfig.AddIndicator(ind);
            foreach (var rule in template.Rules)
                poolConfig.AddRule(rule);
        }
        else
        {
            _logger.LogWarning(
                "Template {TemplateId} no encontrado en DB, usando DefaultPoolTemplateFactory para {Symbol}",
                templateId, symbol);
            poolConfig = _poolTemplateFactory.CreateForSymbol(symbol, mode, interval);
        }

        poolConfig.Activate();

        await repo.AddAsync(poolConfig, ct);
        await unitOfWork.SaveChangesAsync(ct);

        await StartStrategyRunnerAsync(poolConfig, ct);

        if (_runners.TryGetValue(poolConfig.Id, out var runner))
        {
            runner.IsPoolRunner = true;
            runner.AllowNewEntries = false;
            runner.BlockReason = "Cooldown";
        }

        _logger.LogInformation("Pool runner iniciado para {Symbol} (Id={Id})", symbol, poolConfig.Id);
        return Core.Common.Result<Guid, Core.Common.DomainError>.Success(poolConfig.Id);
    }

    public async Task StopPoolRunnerAsync(string symbol, CancellationToken ct = default)
    {
        var runner = _runners.Values.FirstOrDefault(
            r => r.IsPoolRunner && r.Symbol.Value.Equals(symbol, StringComparison.OrdinalIgnoreCase));
        if (runner is null) return;

        var strategyId = runner.StrategyId;

        lock (runner.PoolLock)
        {
            runner.AllowNewEntries = false;
        }

        runner.Cancel();
        _runners.TryRemove(strategyId, out _);
        if (_strategyLocks.TryRemove(strategyId, out var semaphore))
            semaphore.Dispose();
        runner.Dispose();

        try
        {
            using var scope = _scopeFactory.CreateScope();
            var repo = scope.ServiceProvider.GetRequiredService<IStrategyRepository>();
            var unitOfWork = scope.ServiceProvider.GetRequiredService<Core.Interfaces.IUnitOfWork>();
            var strategy = await repo.GetByIdAsync(strategyId, ct);

            if (strategy is not null && strategy.IsActive)
            {
                strategy.Deactivate();
                await repo.UpdateAsync(strategy, ct);
                await unitOfWork.SaveChangesAsync(ct);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "No se pudo desactivar en DB la estrategia Pool {Id} para {Symbol}",
                strategyId, symbol);
        }

        _logger.LogInformation("Pool runner detenido para {Symbol} (Id={Id})", symbol, strategyId);
    }

    public Task SetAllowNewEntriesAsync(string symbol, bool allow, string? blockReason, CancellationToken ct = default)
    {
        var runner = _runners.Values.FirstOrDefault(
            r => r.IsPoolRunner && r.Symbol.Value.Equals(symbol, StringComparison.OrdinalIgnoreCase));
        if (runner is null) return Task.CompletedTask;

        lock (runner.PoolLock)
        {
            var wasActive = runner.AllowNewEntries;
            runner.AllowNewEntries = allow;
            runner.BlockReason = allow ? null : blockReason;
            if (!wasActive && allow)
                runner.EnteredTopKAt = DateTimeOffset.UtcNow;
        }

        return Task.CompletedTask;
    }

    public Task<PoolRunnerInfo?> GetPoolRunnerInfoAsync(string symbol, CancellationToken ct = default)
    {
        var runner = _runners.Values.FirstOrDefault(
            r => r.IsPoolRunner && r.Symbol.Value.Equals(symbol, StringComparison.OrdinalIgnoreCase));
        if (runner is null) return Task.FromResult<PoolRunnerInfo?>(null);

        return Task.FromResult<PoolRunnerInfo?>(BuildPoolRunnerInfo(runner));
    }

    public Task<IReadOnlyList<PoolRunnerInfo>> GetAllPoolRunnerInfosAsync(CancellationToken ct = default)
    {
        var infos = _runners.Values
            .Where(r => r.IsPoolRunner)
            .Select(BuildPoolRunnerInfo)
            .ToList();

        return Task.FromResult<IReadOnlyList<PoolRunnerInfo>>(infos);
    }

    private PoolRunnerInfo BuildPoolRunnerInfo(StrategyRunnerState runner)
    {
        lock (runner.PoolLock)
        {
            var strategy = runner.Strategy;
            decimal? adxValue = null;
            decimal? volumeRatio = null;
            decimal? atrPercent = null;
            decimal? bandWidth = null;
            decimal signalProximity = 0m;
            decimal regimeStability = 1.0m;

            if (strategy is DefaultTradingStrategy dts)
            {
                adxValue = dts.GetAdxIndicator() is { IsReady: true } adx ? adx.Calculate() : null;
                volumeRatio = dts.GetVolumeRatio();
                var atrInd = dts.GetAtrIndicator();
                if (atrInd is { IsReady: true } && atrInd.Calculate() is { } atrVal && runner.LastTickAt != default)
                {
                    var price = dts.CurrentAtrValue is not null ? atrVal : 0m;
                    atrPercent = price > 0 ? (atrVal / price) * 100m : null;
                }
                bandWidth = dts.GetBollingerIndicator()?.BandWidth;
                signalProximity = dts.GetSignalProximity().Value;
                regimeStability = dts.GetRegimeStability();
            }

            var hasOpenPosition = runner.HasCachedOpenPositions;

            return new PoolRunnerInfo(
                runner.Symbol.Value,
                runner.StrategyId,
                strategy.CurrentRegime,
                strategy.IsBullish,
                adxValue,
                volumeRatio,
                atrPercent,
                bandWidth,
                signalProximity,
                regimeStability,
                runner.AllowNewEntries,
                runner.IsPoolRunner,
                runner.EnteredTopKAt,
                runner.BlockReason,
                hasOpenPosition,
                runner.LastTickAt);
        }
    }

    // ── Carga inicial ──────────────────────────────────────────────────────

    private async Task LoadAndStartActiveStrategiesAsync(CancellationToken cancellationToken)
    {
        using var scope = _scopeFactory.CreateScope();
        var strategyRepo = scope.ServiceProvider.GetRequiredService<IStrategyRepository>();
        var strategies   = await strategyRepo.GetActiveStrategiesAsync(cancellationToken);

        foreach (var strategy in strategies.Where(s => s.Origin != StrategyOrigin.Pool))
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
        // Inicializar todas las instancias de estrategia para este strategyId
        await _strategyResolver.InitializeSetAsync(config.Id, config, cancellationToken);

        // La instancia Default es la referencia principal del runner (siempre alimentada, tiene confirmation/BTC EMAs)
        var allStrategies = _strategyResolver.GetAllStrategies(config.Id);
        var tradingStrategy = allStrategies[0]; // Default

        // Precalentar indicadores de todas las instancias con datos históricos
        foreach (var s in allStrategies)
            await WarmUpIndicatorsAsync(s, config, cancellationToken);

        // Evaluar posiciones abiertas inmediatamente al arrancar
        await EvaluateOpenPositionsOnStartupAsync(config, cancellationToken);

        // Suscribir al WebSocket — ticker para SL/TP y klines para indicadores
        await _marketDataService.SubscribeAsync(config.Symbol, cancellationToken);
        await _marketDataService.SubscribeKlinesAsync(config.Symbol, config.Timeframe, cancellationToken);

        // Multi-Timeframe: suscribir al timeframe de confirmación si está configurado
        if (config.ConfirmationTimeframe.HasValue)
            await _marketDataService.SubscribeKlinesAsync(
                config.Symbol, config.ConfirmationTimeframe.Value, cancellationToken);

        // EST-15: para altcoins, suscribir a klines de BTCUSDT para filtro de correlación
        var isBtcPair = config.Symbol.Value.StartsWith("BTC", StringComparison.OrdinalIgnoreCase);
        Symbol? btcSymbol = null;
        CandleInterval btcInterval = default;
        if (!isBtcPair)
        {
            var btcResult = Symbol.Create("BTCUSDT");
            if (btcResult.IsSuccess)
            {
                btcSymbol = btcResult.Value;
                btcInterval = config.ConfirmationTimeframe ?? CandleInterval.FourHours;
                await _marketDataService.SubscribeKlinesAsync(btcSymbol, btcInterval, cancellationToken);
            }
        }

        // Crear y arrancar el runner
        var cts    = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var runner = new StrategyRunnerState(config.Id, config.Name, config.Symbol, tradingStrategy, cts);
        runner.CachedConfig    = config;
        runner.ProcessingTask = Task.Run(async () =>
        {
            var tasks = new List<Task>
            {
                ProcessKlinesLoopAsync(runner, config.Timeframe),
                ProcessTicksLoopAsync(runner)
            };

            // Multi-Timeframe: loop de confirmación en paralelo
            if (config.ConfirmationTimeframe.HasValue)
                tasks.Add(ProcessConfirmationKlinesLoopAsync(runner, config.ConfirmationTimeframe.Value));

            // EST-15: loop de BTC klines para altcoins
            if (btcSymbol is not null)
                tasks.Add(ProcessBtcKlinesLoopAsync(runner, btcSymbol, btcInterval));

            await Task.WhenAll(tasks);
        }, cts.Token);

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
        // 1. Intentar restaurar estado persistido en Redis (sobrevive reinicios)
        if (await TryRestoreIndicatorStateAsync(tradingStrategy, config, cancellationToken))
            return;

        // 2. Fallback: warm-up con datos históricos de Binance
        // CRIT-B fix: calcular warm-up considerando todos los parámetros de periodo
        // según el tipo de indicador. MACD necesita slowPeriod + signalPeriod, no solo "period".
        var maxPeriod = config.Indicators
            .Select(i => GetIndicatorWarmUpPeriod(i))
            .DefaultIfEmpty(0)
            .Max();

        if (maxPeriod <= 0) return;

        var count = maxPeriod + 10;

        // Intentar warm-up con klines (OHLCV) para ATR/Bollinger precisos.
        // Fallback a closes si klines no están disponibles.
        var intervalMinutes = GetIntervalMinutes(config.Timeframe);
        var from = DateTimeOffset.UtcNow.AddMinutes(-(count * intervalMinutes));
        var to   = DateTimeOffset.UtcNow;
        var klinesResult = await _marketDataService.GetKlinesAsync(
            config.Symbol, from, to, config.Timeframe, cancellationToken);

        if (klinesResult.IsSuccess && klinesResult.Value.Count > 0)
        {
            foreach (var kline in klinesResult.Value)
            {
                // TRADE-1/CRIT-1 fix: alimentar indicadores directamente con OHLC.
                // Esto permite que el ATR calcule True Range real (max(H-L, |H-pC|, |L-pC|))
                // en vez de la aproximación |close-prevClose|.
                tradingStrategy.WarmUpOhlc(kline.High, kline.Low, kline.Close, kline.Volume);
            }

            // Sincronizar estado previo para evitar señales falsas en la primera vela real
            if (tradingStrategy is DefaultTradingStrategy defaultStrategy)
                defaultStrategy.SyncPreviousIndicatorState();

            _logger.LogDebug(
                "Warm-up completado para '{Name}': {Count} klines OHLCV procesadas",
                config.Name, klinesResult.Value.Count);
        }
        else
        {
            // Fallback: solo cierres (ATR será menos preciso)
            var closesResult = await _marketDataService.GetHistoricalClosesAsync(
                config.Symbol, count, cancellationToken);

            if (closesResult.IsFailure)
            {
                _logger.LogWarning(
                    "No se pudieron obtener datos históricos para {Symbol}: {Error}",
                    config.Symbol.Value, closesResult.Error.Message);
                return;
            }

            foreach (var close in closesResult.Value)
                tradingStrategy.WarmUpPrice(close);

            // Sincronizar estado previo para evitar señales falsas en la primera vela real
            if (tradingStrategy is DefaultTradingStrategy defaultStrategy)
                defaultStrategy.SyncPreviousIndicatorState();

            _logger.LogDebug(
                "Warm-up completado para '{Name}': {Count} cierres históricos procesados (fallback, ATR puede ser impreciso)",
                config.Name, closesResult.Value.Count);
        }

        // ALTA-NEW-2: warm-up de la EMA de confirmación HTF
        await WarmUpConfirmationEmaAsync(tradingStrategy, config, cancellationToken);

        // EST-15: warm-up de la EMA de BTC para filtro de correlación en altcoins
        await WarmUpBtcEmaAsync(tradingStrategy, config, cancellationToken);
    }

    /// <summary>
    /// ALTA-NEW-2: Precalienta la EMA de confirmación HTF con klines históricas.
    /// Sin esto, la EMA necesita N×HTF velas en tiempo real (ej: EMA 20 en 4H = 80h).
    /// </summary>
    private async Task WarmUpConfirmationEmaAsync(
        ITradingStrategy tradingStrategy,
        TradingStrategy config,
        CancellationToken cancellationToken)
    {
        if (!config.ConfirmationTimeframe.HasValue)
            return;

        var htfInterval = config.ConfirmationTimeframe.Value;
        var htfIntervalMinutes = GetIntervalMinutes(htfInterval);
        var htfCount = config.RiskConfig.ConfirmationEmaPeriod + 10;
        var htfFrom = DateTimeOffset.UtcNow.AddMinutes(-(htfCount * htfIntervalMinutes));

        var htfKlines = await _marketDataService.GetKlinesAsync(
            config.Symbol, htfFrom, DateTimeOffset.UtcNow, htfInterval, cancellationToken);

        if (htfKlines.IsFailure || htfKlines.Value.Count == 0)
        {
            _logger.LogWarning(
                "No se pudieron obtener klines HTF ({Interval}) para warm-up de confirmación EMA de '{Name}'",
                htfInterval, config.Name);
            return;
        }

        foreach (var k in htfKlines.Value)
        {
            tradingStrategy.ProcessConfirmationKline(new KlineClosedEvent(
                config.Symbol,
                htfInterval,
                k.Open, k.High, k.Low, k.Close, k.Volume,
                k.OpenTime,
                k.OpenTime.AddMinutes(htfIntervalMinutes)));
        }

        _logger.LogDebug(
            "Warm-up HTF completado para '{Name}': {Count} klines de {Interval} procesadas (confirmación EMA {Period})",
            config.Name, htfKlines.Value.Count, htfInterval, config.RiskConfig.ConfirmationEmaPeriod);
    }

    /// <summary>
    /// EST-15: Precalienta la EMA de BTC con klines históricas de BTCUSDT.
    /// Solo aplica para estrategias que operan altcoins (no BTC).
    /// </summary>
    private async Task WarmUpBtcEmaAsync(
        ITradingStrategy tradingStrategy,
        TradingStrategy config,
        CancellationToken cancellationToken)
    {
        if (config.Symbol.Value.StartsWith("BTC", StringComparison.OrdinalIgnoreCase))
            return;

        var btcSymbolResult = Symbol.Create("BTCUSDT");
        if (btcSymbolResult.IsFailure)
            return;

        var btcInterval = config.ConfirmationTimeframe ?? CandleInterval.FourHours;
        var btcIntervalMinutes = GetIntervalMinutes(btcInterval);
        var btcCount = config.RiskConfig.ConfirmationEmaPeriod + 10;
        var btcFrom = DateTimeOffset.UtcNow.AddMinutes(-(btcCount * btcIntervalMinutes));

        var btcKlines = await _marketDataService.GetKlinesAsync(
            btcSymbolResult.Value, btcFrom, DateTimeOffset.UtcNow, btcInterval, cancellationToken);

        if (btcKlines.IsFailure || btcKlines.Value.Count == 0)
        {
            _logger.LogWarning(
                "No se pudieron obtener klines de BTCUSDT ({Interval}) para warm-up de correlación BTC de '{Name}'",
                btcInterval, config.Name);
            return;
        }

        foreach (var k in btcKlines.Value)
        {
            tradingStrategy.ProcessBtcKline(new KlineClosedEvent(
                btcSymbolResult.Value,
                btcInterval,
                k.Open, k.High, k.Low, k.Close, k.Volume,
                k.OpenTime,
                k.OpenTime.AddMinutes(btcIntervalMinutes)));
        }

        _logger.LogDebug(
            "Warm-up BTC completado para '{Name}': {Count} klines de BTCUSDT {Interval} procesadas",
            config.Name, btcKlines.Value.Count, btcInterval);
    }

    /// <summary>
    /// Intenta restaurar el estado de los indicadores desde Redis.
    /// Si los parámetros cambiaron, descarta el estado guardado.
    /// </summary>
    private async Task<bool> TryRestoreIndicatorStateAsync(
        ITradingStrategy tradingStrategy,
        TradingStrategy config,
        CancellationToken cancellationToken)
    {
        try
        {
            var savedStates = await _indicatorStateStore.RestoreAsync(config.Id, cancellationToken);
            if (savedStates is null || savedStates.Count == 0)
                return false;

            // Acceder a los indicadores internos de la estrategia
            if (tradingStrategy is not DefaultTradingStrategy defaultStrategy)
                return false;

            var restored = defaultStrategy.RestoreIndicatorStates(savedStates);

            if (restored)
            {
                _logger.LogInformation(
                    "Indicadores de '{Name}' ({Id}) restaurados desde Redis — warm-up omitido",
                    config.Name, config.Id);
            }

            return restored;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Error restaurando estado de indicadores para '{Name}'. Ejecutando warm-up normal.",
                config.Name);
            return false;
        }
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
            var orderRepo = scope.ServiceProvider.GetRequiredService<IOrderRepository>();

            // ATR no disponible al reiniciar (indicadores no precalentados aún); usar stop-loss porcentual
            foreach (var position in openPositions)
            {
                position.UpdatePrice(currentPrice);

                var exitResult = await ruleEngine.EvaluateExitRulesAsync(
                    config, position, currentPrice, cancellationToken, atrValue: null);

                if (exitResult.IsSuccess && exitResult.Value is { } exitOrder)
                {
                    // Verificar idempotencia: no duplicar orden de cierre si ya hay una pendiente
                    var hasPending = await orderRepo.HasPendingCloseOrderAsync(
                        config.Id, exitOrder.Symbol, exitOrder.Side, cancellationToken);
                    if (hasPending)
                    {
                        _logger.LogInformation(
                            "Orden de cierre duplicada evitada al reiniciar para posición {PosId} ({Side} {Symbol})",
                            position.Id, exitOrder.Side, exitOrder.Symbol.Value);
                        continue;
                    }

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
        var checkIntervalSeconds = _globalRisk.WatchdogIntervalSeconds;
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

    // ── Persistencia de estado de indicadores en Redis ─────────────────────

    /// <summary>
    /// Verifica periódicamente el drawdown diario de la cuenta.
    /// Si supera el límite configurado, activa el circuit breaker global para detener todo el trading.
    /// </summary>
    private async Task RunDrawdownCheckerAsync(CancellationToken cancellationToken)
    {
        var checkIntervalSeconds = _globalRisk.DrawdownCheckIntervalSeconds;

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(checkIntervalSeconds), cancellationToken);

                if (_circuitBreaker.IsOpen || _runners.IsEmpty) continue;

                try
                {
                    using var scope      = _scopeFactory.CreateScope();
                    var riskManager      = scope.ServiceProvider.GetRequiredService<IRiskManager>();
                    var (isTriggered, drawdownPercent) = await riskManager.CheckAccountDrawdownAsync(cancellationToken);

                    if (isTriggered)
                    {
                        _circuitBreaker.Trip(
                            $"Drawdown de cuenta {drawdownPercent:F1}% superó el límite configurado. Trading detenido automáticamente.");

                        var notifier = scope.ServiceProvider.GetService<ITradingNotifier>();
                        if (notifier is not null)
                            await notifier.NotifyAlertAsync(
                                $"🛑 CIRCUIT BREAKER — Drawdown de cuenta {drawdownPercent:F1}%. Todo el trading ha sido detenido.",
                                cancellationToken);
                    }
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Error en drawdown checker");
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Shutdown normal
        }
    }

    // ── Persistencia de estado de indicadores en Redis ─────────────────────

    /// <summary>
    /// Guarda el estado de los indicadores de una estrategia en Redis.
    /// Si falla, solo loguea warning — no interrumpe el trading.
    /// </summary>
    private async Task SaveIndicatorStateAsync(StrategyRunnerState runner)
    {
        try
        {
            if (runner.Strategy is not DefaultTradingStrategy defaultStrategy)
                return;

            var states = defaultStrategy.SaveIndicatorStates();
            if (states.Count == 0) return;

            await _indicatorStateStore.SaveAsync(runner.StrategyId, states);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Error guardando estado de indicadores para '{Name}' ({Id}). Se reintentará en la siguiente vela.",
                runner.StrategyName, runner.StrategyId);
        }
    }

    // ── Loop de klines (indicadores + señales) ──────────────────────────────

    /// <summary>
    /// Consume velas cerradas del WebSocket y las procesa con <see cref="ITradingStrategy.ProcessKlineAsync"/>.
    /// Las señales generadas aquí son las que producen órdenes de entrada.
    /// </summary>
    private async Task ProcessKlinesLoopAsync(StrategyRunnerState runner, CandleInterval interval)
    {
        var token = runner.CancellationTokenSource.Token;

        _logger.LogDebug("Kline loop iniciado para '{Name}' ({Id})", runner.StrategyName, runner.StrategyId);

        try
        {
            await foreach (var kline in _marketDataService.GetKlineStreamAsync(runner.Symbol, interval, token))
            {
                if (token.IsCancellationRequested) break;
                if (_isPaused || _circuitBreaker.IsOpen) continue;

                try
                {
                    var strategyLock = _strategyLocks.GetOrAdd(runner.StrategyId, _ => new SemaphoreSlim(1, 1));
                    await strategyLock.WaitAsync(token);
                    try
                    {
                        await ProcessSingleKlineAsync(runner, kline, token);

                        // Persistir estado de indicadores en Redis tras cada vela cerrada.
                        // Si falla, no interrumpe el trading — se reintenta en la siguiente vela.
                        await SaveIndicatorStateAsync(runner);
                    }
                    finally
                    {
                        strategyLock.Release();
                    }
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex)
                {
                    _logger.LogError(ex,
                        "Error procesando kline para '{Name}' ({Id})",
                        runner.StrategyName, runner.StrategyId);
                }
            }
        }
        catch (OperationCanceledException) { /* shutdown normal */ }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Kline loop terminado inesperadamente para '{Name}' ({Id})",
                runner.StrategyName, runner.StrategyId);
        }
        finally
        {
            _logger.LogDebug("Kline loop finalizado para '{Name}' ({Id})", runner.StrategyName, runner.StrategyId);
        }
    }

    private async Task ClosePositionsOnRegimeChangeAsync(
        StrategyRunnerState runner, CancellationToken cancellationToken)
    {
        using var scope = _scopeFactory.CreateScope();
        var positionRepo = scope.ServiceProvider.GetRequiredService<IPositionRepository>();
        var orderService = scope.ServiceProvider.GetRequiredService<IOrderService>();
        var notifier = scope.ServiceProvider.GetService<ITradingNotifier>();
        var config = runner.CachedConfig;
        if (config is null) return;

        var openPositions = await runner.GetOpenPositionsAsync(positionRepo, cancellationToken);
        if (openPositions.Count == 0) return;

        _logger.LogInformation(
            "Cerrando {Count} posiciones de '{Name}' por transición a Indefinite",
            openPositions.Count, runner.StrategyName);

        foreach (var position in openPositions)
        {
            var closeSide = position.Side == OrderSide.Buy ? OrderSide.Sell : OrderSide.Buy;
            var orderResult = Order.Create(
                config.Id, position.Symbol, closeSide, OrderType.Market,
                position.Quantity, config.Mode);

            if (orderResult.IsFailure) continue;

            var placeResult = await orderService.PlaceOrderAsync(orderResult.Value, cancellationToken);
            if (placeResult.IsSuccess)
            {
                runner.OrdersPlaced++;
                runner.InvalidatePositionCache();

                if (notifier is not null)
                    await notifier.NotifyAlertAsync(
                        $"Posición de '{config.Name}' cerrada por régimen Indefinite",
                        cancellationToken);
            }
        }
    }

    private ITradingStrategy? ResolveActiveStrategy(StrategyRunnerState runner, KlineClosedEvent kline)
    {
        var config = runner.CachedConfig;
        if (config is null)
            return runner.Strategy;

        var resolved = _strategyResolver.Resolve(runner.StrategyId, runner.Strategy.CurrentRegime, config);
        if (resolved is null)
            return null;

        resolved.CurrentRegime = runner.Strategy.CurrentRegime;
        return resolved;
    }

    private void FeedKlineToAllStrategies(StrategyRunnerState runner, KlineClosedEvent kline)
    {
        var allStrategies = _strategyResolver.GetAllStrategies(runner.StrategyId);
        foreach (var s in allStrategies)
        {
            if (s is DefaultTradingStrategy dts)
            {
                dts.WarmUpOhlc(kline.High, kline.Low, kline.Close, kline.Volume);
            }
        }
    }

    private void FeedKlineToOtherStrategies(StrategyRunnerState runner, KlineClosedEvent kline, ITradingStrategy active)
    {
        var allStrategies = _strategyResolver.GetAllStrategies(runner.StrategyId);
        foreach (var s in allStrategies)
        {
            if (ReferenceEquals(s, active)) continue;
            if (s is DefaultTradingStrategy dts)
            {
                dts.WarmUpOhlc(kline.High, kline.Low, kline.Close, kline.Volume);
            }
        }
    }

    private async Task ProcessSingleKlineAsync(
        StrategyRunnerState runner,
        KlineClosedEvent kline,
        CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();

        runner.EmaAlignment.Update(kline.Close);
        runner.HhLlDetector.Update(kline.High, kline.Low);

        var config = runner.CachedConfig;
        if (config is not null)
        {
            var adx = runner.Strategy is DefaultTradingStrategy dts
                ? dts.GetAdxIndicator() : null;
            var bb = runner.Strategy is DefaultTradingStrategy dts2
                ? dts2.GetBollingerIndicator() : null;
            var atr = runner.Strategy is DefaultTradingStrategy dts3
                ? dts3.GetAtrIndicator() : null;
            var volRatio = runner.Strategy is DefaultTradingStrategy dts4
                ? dts4.GetVolumeRatio() : null;

            var raw = runner.RegimeDetector.Detect(
                adx, bb, atr, kline.Close,
                config.RiskConfig.AdxTrendingThreshold,
                config.RiskConfig.AdxRangingThreshold,
                config.RiskConfig.HighVolatilityBandWidthPercent,
                config.RiskConfig.HighVolatilityAtrPercent,
                runner.EmaAlignment,
                runner.HhLlDetector,
                volRatio,
                config.RiskConfig.IndefiniteAdxThreshold);

            var confirmed = runner.RegimeDetector.GetConfirmedRegime(
                raw.Regime, config.RiskConfig.RegimeConfirmationCandles);

            var finalResult = config.RiskConfig.UseHysteresis
                ? MarketRegimeDetector.ApplyHysteresis(
                    raw with { Regime = confirmed }, runner.PreviousRegime,
                    config.RiskConfig.AdxTrendingThreshold, config.RiskConfig.AdxRangingThreshold)
                : raw with { Regime = confirmed };

            if (finalResult.Regime != MarketRegime.Unknown)
            {
                if (runner.PreviousRegime != finalResult.Regime)
                    runner.TrackingState.OnRegimeChange();
                runner.PreviousRegime = finalResult.Regime;
            }

            runner.Strategy.CurrentRegime = finalResult.Regime;

            if (finalResult.Regime == MarketRegime.Indefinite)
            {
                _logger.LogDebug(
                    "Régimen Indefinite para '{Name}' — bloqueo total de señales",
                    runner.StrategyName);

                if (config.RiskConfig.ExitOnRegimeChange
                    && runner.PreviousRegime is not MarketRegime.Indefinite and not MarketRegime.Unknown)
                {
                    await ClosePositionsOnRegimeChangeAsync(runner, cancellationToken);
                }

                // Alimentar indicadores de todas las instancias aunque no haya señal
                FeedKlineToAllStrategies(runner, kline);
                return;
            }
        }

        // Resolver la estrategia activa según el régimen detectado
        var activeStrategy = ResolveActiveStrategy(runner, kline);
        if (activeStrategy is null)
        {
            FeedKlineToAllStrategies(runner, kline);
            return;
        }

        var signalResult = await activeStrategy.ProcessKlineAsync(kline, cancellationToken);

        // Alimentar indicadores del resto de instancias (la activa ya se alimentó via ProcessKlineAsync)
        FeedKlineToOtherStrategies(runner, kline, activeStrategy);

        var strategy = runner.CachedConfig;
        if (strategy is null || !strategy.IsActive) return;

        using var scope  = _scopeFactory.CreateScope();
        var ruleEngine   = scope.ServiceProvider.GetRequiredService<IRuleEngine>();
        var orderService = scope.ServiceProvider.GetRequiredService<IOrderService>();
        var positionRepo = scope.ServiceProvider.GetRequiredService<IPositionRepository>();
        var notifier     = scope.ServiceProvider.GetService<ITradingNotifier>();

        // TRADE-2 fix: evaluar reglas de salida basadas en indicadores al cierre de vela.
        // Esto complementa el tick loop que solo evalúa SL/TP/trailing (precio puro).
        var currentAtr = activeStrategy.CurrentAtrValue;
        var currentSnapshot = activeStrategy.GetCurrentSnapshot();
        var lastPrice = Price.Create(kline.Close);

        if (lastPrice.IsSuccess)
        {
            var openPositions = await runner.GetOpenPositionsAsync(positionRepo, cancellationToken);

            foreach (var position in openPositions)
            {
                var exitResult = await ruleEngine.EvaluateExitRulesAsync(
                    strategy, position, lastPrice.Value, cancellationToken,
                    atrValue: currentAtr,
                    indicatorSnapshot: currentSnapshot,
                    evaluateIndicatorRules: true); // Kline loop: evaluar reglas de indicadores

                if (exitResult.IsSuccess && exitResult.Value is { } exitOrder)
                {
                    var orderRepo = scope.ServiceProvider.GetRequiredService<IOrderRepository>();
                    var hasPending = await orderRepo.HasPendingCloseOrderAsync(
                        runner.StrategyId, exitOrder.Symbol, exitOrder.Side, cancellationToken);
                    if (hasPending) continue;

                    var exitPlaceResult = await orderService.PlaceOrderAsync(exitOrder, cancellationToken);
                    if (exitPlaceResult.IsSuccess)
                    {
                        runner.OrdersPlaced++;
                        runner.InvalidatePositionCache();
                        _logger.LogInformation(
                            "Orden de salida (kline/indicadores) por '{Name}': {Side} {Symbol} (posición {PosId})",
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

        // ── Pool v2: guard — no abrir nuevas entradas si el runner lo tiene bloqueado ──
        if (!runner.AllowNewEntries)
            return;
        if (runner.IsPoolRunner && runner.EnteredTopKAt is not null
            && DateTimeOffset.UtcNow - runner.EnteredTopKAt < runner.MinTimeInTopKBeforeEntry)
            return;

        // Evaluar señal de entrada
        if (signalResult.IsFailure || signalResult.Value is null) return;

        var signal = signalResult.Value;
        var correlationId = Guid.NewGuid().ToString("N")[..12];

        using var _ = _logger.BeginScope(new Dictionary<string, object>
        {
            ["CorrelationId"] = correlationId,
            ["StrategyId"]    = runner.StrategyId,
            ["Symbol"]        = runner.Symbol.Value
        });

        runner.SignalsGenerated++;
        _metrics.RecordSignalGenerated(runner.StrategyName, runner.Symbol.Value, signal.Direction.ToString());

        if (notifier is not null)
            await notifier.NotifySignalGeneratedAsync(signal, cancellationToken);

        // Verificación anti-duplicado adaptada a Spot (solo Long):
        // - Buy: no abrir si ya hay posición Long abierta para este símbolo
        // - Sell: no operar si NO hay posición Long abierta que cerrar (Spot no soporta shorts)
        var existingPositions = await runner.GetOpenPositionsAsync(positionRepo, cancellationToken);
        if (signal.Direction == OrderSide.Buy)
        {
            if (existingPositions.Any(p => p.Symbol == signal.Symbol && p.Side == OrderSide.Buy))
                return;
        }
        else // Sell
        {
            var longPosition = existingPositions
                .FirstOrDefault(p => p.Symbol == signal.Symbol && p.Side == OrderSide.Buy);

            if (longPosition is null)
            {
                _logger.LogDebug(
                    "Señal Sell descartada para '{Name}': no hay posición Long abierta en {Symbol}",
                    runner.StrategyName, signal.Symbol.Value);
                return;
            }

            // EST-18: señal Sell con posición Long en ganancia → cierre proactivo.
            // Saltea filtros de confirmación/BTC (queremos cerrar antes de reversión).
            // Umbral mínimo: al menos 0.5% de ganancia neta para evitar cierres
            // que después de fees reales (taker + slippage) resultan en pérdida.
            var minProactiveProfitPct = Math.Max(0.5m, strategy.RiskConfig.StopLossPercent.Value * 0.25m);
            if (longPosition.UnrealizedPnLPercent >= minProactiveProfitPct)
            {
                _logger.LogInformation(
                    "EST-18: cierre proactivo de posición {PosId} por señal Sell ({Source}). PnL={PnL:F2}%",
                    longPosition.Id, signal.IndicatorSnapshot, longPosition.UnrealizedPnLPercent);

                var exitSide = OrderSide.Sell;
                var exitOrderResult = Order.Create(
                    strategy.Id, longPosition.Symbol, exitSide,
                    OrderType.Market, longPosition.Quantity, strategy.Mode,
                    estimatedPrice: signal.CurrentPrice);

                if (exitOrderResult.IsSuccess)
                {
                    var exitOrder = exitOrderResult.Value;
                    var orderRepo = scope.ServiceProvider.GetRequiredService<IOrderRepository>();
                    var hasPending = await orderRepo.HasPendingCloseOrderAsync(
                        runner.StrategyId, exitOrder.Symbol, exitOrder.Side, cancellationToken);
                    if (!hasPending)
                    {
                        var proactivePlaceResult = await orderService.PlaceOrderAsync(exitOrder, cancellationToken);
                        if (proactivePlaceResult.IsSuccess)
                        {
                            runner.OrdersPlaced++;
                            runner.InvalidatePositionCache();
                            _logger.LogInformation(
                                "Orden de cierre proactivo por '{Name}': Sell {Symbol} (posición {PosId}, PnL={PnL:F2}%)",
                                runner.StrategyName, exitOrder.Symbol.Value, longPosition.Id,
                                longPosition.UnrealizedPnLPercent);

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
                return;
            }
        }

        // Multi-Timeframe: verificar que la tendencia del HTF confirma la dirección
        if (!runner.Strategy.IsConfirmationAligned(signal.Direction))
        {
            _logger.LogDebug(
                "Señal {Direction} en '{Name}' rechazada: HTF no confirma la dirección",
                signal.Direction, runner.StrategyName);
            return;
        }

        if (runner.HtfHhLlDetector.IsReady())
        {
            if (runner.HtfHhLlDetector.HasHigherHighs() && signal.Direction != OrderSide.Buy)
            {
                _logger.LogDebug(
                    "Señal {Direction} en '{Name}' rechazada: HTF muestra HH (solo Buy permitido)",
                    signal.Direction, runner.StrategyName);
                return;
            }
            if (runner.HtfHhLlDetector.HasLowerLows() && signal.Direction != OrderSide.Sell)
            {
                _logger.LogDebug(
                    "Señal {Direction} en '{Name}' rechazada: HTF muestra LL (solo Sell permitido)",
                    signal.Direction, runner.StrategyName);
                return;
            }
        }

        // EST-15: verificar que BTC confirma la dirección (solo altcoins)
        if (!runner.Strategy.IsBtcAligned(signal.Direction))
        {
            _logger.LogDebug(
                "Señal {Direction} en '{Name}' rechazada: BTC no confirma la dirección",
                signal.Direction, runner.StrategyName);
            return;
        }

        var orderResult = await ruleEngine.EvaluateAsync(strategy, signal, cancellationToken);
        if (orderResult.IsFailure || orderResult.Value is null) return;

        var order = orderResult.Value;

        // Position sizing con ATR si está habilitado
        if (strategy.RiskConfig.UseAtrSizing && signal.AtrValue is > 0)
        {
            var accountService = scope.ServiceProvider.GetRequiredService<IAccountService>();
            var balanceResult = await accountService.GetAvailableBalanceAsync("USDT", cancellationToken);
            if (balanceResult.IsSuccess)
            {
                var sizing = PositionSizer.Calculate(
                    balanceResult.Value,
                    strategy.RiskConfig.RiskPercentPerTrade / 100m,
                    signal.AtrValue.Value,
                    strategy.RiskConfig.AtrMultiplier,
                    signal.CurrentPrice.Value,
                    strategy.RiskConfig.MaxOrderAmountUsdt);

                var adjustedQty = Quantity.Create(sizing.QuantityBaseAsset);
                if (adjustedQty.IsSuccess)
                {
                    var adjustedOrder = Order.Create(
                        order.StrategyId, order.Symbol, order.Side, order.Type,
                        adjustedQty.Value, order.Mode, order.LimitPrice, order.StopPrice,
                        estimatedPrice: signal.CurrentPrice);

                    if (adjustedOrder.IsSuccess)
                        order = adjustedOrder.Value;
                }
            }
        }

        var placeResult = await orderService.PlaceOrderAsync(order, cancellationToken);
        if (placeResult.IsSuccess)
        {
            var explanation = TradeExplainerService.BuildEntryExplanation(
                signalSource: signal.IndicatorSnapshot.Split(" | ").FirstOrDefault() ?? "Unknown",
                direction: signal.Direction,
                entryPrice: signal.CurrentPrice.Value,
                marketRegime: runner.Strategy.CurrentRegime.ToString(),
                adxValue: null,
                adxBullish: null,
                indicatorSnapshot: signal.IndicatorSnapshot,
                confirmationsObtained: 0,
                confirmationsTotal: 0,
                confirmationDetails: TradeExplainerService.BuildConfirmationDetails(signal.IndicatorSnapshot, 0, 0),
                filtersPassed: TradeExplainerService.BuildFiltersList(
                    htfAligned: runner.Strategy.IsConfirmationAligned(signal.Direction),
                    btcAligned: runner.Strategy.IsBtcAligned(signal.Direction),
                    cooldownPassed: true),
                riskCheckSummary: strategy.RiskConfig.UseAtrSizing && signal.AtrValue is > 0
                    ? $"ATR sizing: ATR={signal.AtrValue:F4}, multiplier={strategy.RiskConfig.AtrMultiplier}"
                    : $"Fixed: MaxOrder={strategy.RiskConfig.MaxOrderAmountUsdt} USDT");
            order.SetExplanation(explanation);

            runner.OrdersPlaced++;
            runner.InvalidatePositionCache();
            _metrics.RecordTickToOrderLatency(sw.Elapsed.TotalMilliseconds, runner.Symbol.Value);
            _logger.LogInformation(
                "Orden de entrada (kline) por '{Name}': {Side} {Qty} {Symbol}",
                runner.StrategyName, order.Side, order.Quantity.Value, order.Symbol.Value);

            if (notifier is not null)
                await notifier.NotifyOrderExecutedAsync(
                    new OrderPlacedEvent(
                        order.Id, order.StrategyId, order.Symbol,
                        order.Side, order.Type, order.Quantity,
                        order.LimitPrice, order.IsPaperTrade),
                    cancellationToken);
        }
    }

    // ── Loop de confirmación Multi-Timeframe ─────────────────────────────

    /// <summary>
    /// Consume velas cerradas del timeframe de confirmación y las procesa
    /// para actualizar la tendencia macro (EMA del HTF).
    /// No genera señales — solo alimenta <see cref="ITradingStrategy.ProcessConfirmationKline"/>.
    /// </summary>
    private async Task ProcessConfirmationKlinesLoopAsync(
        StrategyRunnerState runner,
        CandleInterval confirmationInterval)
    {
        var token = runner.CancellationTokenSource.Token;

        _logger.LogDebug(
            "Confirmation kline loop iniciado para '{Name}' ({Id}) [{Interval}]",
            runner.StrategyName, runner.StrategyId, confirmationInterval);

        try
        {
            await foreach (var kline in _marketDataService.GetKlineStreamAsync(
                               runner.Symbol, confirmationInterval, token))
            {
                if (token.IsCancellationRequested) break;

                try
                {
                    foreach (var s in _strategyResolver.GetAllStrategies(runner.StrategyId))
                        s.ProcessConfirmationKline(kline);
                    runner.HtfHhLlDetector.Update(kline.High, kline.Low);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex,
                        "Error procesando confirmation kline para '{Name}' ({Id})",
                        runner.StrategyName, runner.StrategyId);
                }
            }
        }
        catch (OperationCanceledException) { /* shutdown normal */ }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Confirmation kline loop terminado inesperadamente para '{Name}' ({Id})",
                runner.StrategyName, runner.StrategyId);
        }
    }

    // ── EST-15: Loop de BTC klines para filtro de correlación ────────────

    private async Task ProcessBtcKlinesLoopAsync(
        StrategyRunnerState runner,
        Symbol btcSymbol,
        CandleInterval btcInterval)
    {
        var token = runner.CancellationTokenSource.Token;

        _logger.LogDebug(
            "BTC correlation kline loop iniciado para '{Name}' ({Id}) [{Interval}]",
            runner.StrategyName, runner.StrategyId, btcInterval);

        try
        {
            await foreach (var kline in _marketDataService.GetKlineStreamAsync(
                               btcSymbol, btcInterval, token))
            {
                if (token.IsCancellationRequested) break;

                try
                {
                    foreach (var s in _strategyResolver.GetAllStrategies(runner.StrategyId))
                        s.ProcessBtcKline(kline);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex,
                        "Error procesando BTC kline para '{Name}' ({Id})",
                        runner.StrategyName, runner.StrategyId);
                }
            }
        }
        catch (OperationCanceledException) { /* shutdown normal */ }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "BTC correlation kline loop terminado inesperadamente para '{Name}' ({Id})",
                runner.StrategyName, runner.StrategyId);
        }
    }

    // ── Loop principal de ticks (SL/TP + dashboard) ───────────────────────

    private async Task ProcessTicksLoopAsync(StrategyRunnerState runner)
    {
        var token = runner.CancellationTokenSource.Token;
        const int maxConsecutiveErrors = 10;
        var consecutiveErrors = 0;

        // Cooldown progresivo: 30s → 1m → 5m → 15m → 30m
        int[] cooldownSeconds = [30, 60, 300, 900, 1800];
        var cooldownIndex = 0;
        var totalErrorBursts = 0;
        const int maxErrorBurstsPerHour = 5;
        var firstBurstAt = DateTimeOffset.MinValue;

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
                    // Serializar procesamiento para evitar órdenes duplicadas por race condition
                    var strategyLock = _strategyLocks.GetOrAdd(runner.StrategyId, _ => new SemaphoreSlim(1, 1));
                    await strategyLock.WaitAsync(token);
                    try
                    {
                        await ProcessSingleTickAsync(runner, tick, token);
                    }
                    finally
                    {
                        strategyLock.Release();
                    }
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
                        totalErrorBursts++;

                        // Reset del contador de ráfagas cada hora
                        if (firstBurstAt == DateTimeOffset.MinValue)
                            firstBurstAt = DateTimeOffset.UtcNow;
                        else if (DateTimeOffset.UtcNow - firstBurstAt > TimeSpan.FromHours(1))
                        {
                            totalErrorBursts = 1;
                            cooldownIndex = 0;
                            firstBurstAt = DateTimeOffset.UtcNow;
                        }

                        // Agotar todos los reintentos en 1 hora → marcar como Error permanente
                        if (totalErrorBursts > maxErrorBurstsPerHour)
                        {
                            _logger.LogCritical(
                                "Estrategia '{Name}' ({Id}) marcada como Error: {Bursts} ráfagas de errores en 1 hora",
                                runner.StrategyName, runner.StrategyId, totalErrorBursts);

                            await MarkStrategyAsErrorAsync(runner.StrategyId, token);
                            break;
                        }

                        // Cooldown progresivo antes de reintentar
                        var cooldown = cooldownSeconds[Math.Min(cooldownIndex, cooldownSeconds.Length - 1)];
                        cooldownIndex++;

                        _logger.LogWarning(
                            "Estrategia '{Name}' ({Id}): {Errors} errores consecutivos. " +
                            "Cooldown {Cooldown}s antes de reintentar (ráfaga {Burst}/{MaxBursts})",
                            runner.StrategyName, runner.StrategyId,
                            consecutiveErrors, cooldown, totalErrorBursts, maxErrorBurstsPerHour);

                        consecutiveErrors = 0;
                        await Task.Delay(TimeSpan.FromSeconds(cooldown), token);
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
        var sw = Stopwatch.StartNew();

        runner.TicksProcessed++;
        runner.LastTickAt = tick.Timestamp;
        _metrics.RecordTickProcessed(runner.Symbol.Value);

        // Circuit breaker global — si está abierto, no procesar
        if (_circuitBreaker.IsOpen) return;

        // Scope para servicios scoped
        using var scope       = _scopeFactory.CreateScope();
        var ruleEngine        = scope.ServiceProvider.GetRequiredService<IRuleEngine>();
        var orderService      = scope.ServiceProvider.GetRequiredService<IOrderService>();
        var positionRepo      = scope.ServiceProvider.GetRequiredService<IPositionRepository>();
        var notifier          = scope.ServiceProvider.GetService<ITradingNotifier>();

        // Notificar tick al frontend vía SignalR
        if (notifier is not null)
            await notifier.NotifyMarketTickAsync(tick, cancellationToken);

        var strategy = runner.CachedConfig;
        if (strategy is null || !strategy.IsActive) return;

        // Ticks ahora solo evalúan reglas de salida (SL/TP) para posiciones abiertas
        var openPositions = await runner.GetOpenPositionsAsync(positionRepo, cancellationToken);

        var currentAtr = runner.Strategy.CurrentAtrValue;
        var positionPriceChanged = false;

        foreach (var position in openPositions)
        {
            var prevHigh = position.HighestPriceSinceEntry.Value;
            var prevLow  = position.LowestPriceSinceEntry.Value;

            position.UpdatePrice(tick.LastPrice);

            // Rastrear si algún peak price cambió para persistir al final
            if (position.HighestPriceSinceEntry.Value != prevHigh
                || position.LowestPriceSinceEntry.Value != prevLow)
            {
                positionPriceChanged = true;
            }

            var exitResult = await ruleEngine.EvaluateExitRulesAsync(
                strategy, position, tick.LastPrice, cancellationToken,
                atrValue: currentAtr,
                indicatorSnapshot: runner.Strategy.GetCurrentSnapshot(),
                evaluateIndicatorRules: false); // TRADE-2: tick loop solo evalúa SL/TP/trailing

            if (exitResult.IsSuccess && exitResult.Value is { } exitOrder)
            {
                // Verificar idempotencia: no duplicar orden de cierre si ya hay una pendiente
                var orderRepo = scope.ServiceProvider.GetRequiredService<IOrderRepository>();
                var hasPending = await orderRepo.HasPendingCloseOrderAsync(
                    runner.StrategyId, exitOrder.Symbol, exitOrder.Side, cancellationToken);
                if (hasPending)
                {
                    _logger.LogDebug(
                        "Orden de cierre duplicada evitada para posición {PosId} ({Side} {Symbol})",
                        position.Id, exitOrder.Side, exitOrder.Symbol.Value);
                    continue;
                }

                var placeResult = await orderService.PlaceOrderAsync(exitOrder, cancellationToken);
                if (placeResult.IsSuccess)
                {
                    runner.OrdersPlaced++;
                    runner.InvalidatePositionCache();
                    _metrics.RecordTickToOrderLatency(sw.Elapsed.TotalMilliseconds, runner.Symbol.Value);
                    _logger.LogInformation(
                        "Orden de salida colocada por '{Name}': {Side} {Symbol} (posición {PosId})",
                        runner.StrategyName, exitOrder.Side, exitOrder.Symbol.Value, position.Id);

                    // EST-17: notificar a la estrategia si fue un stop-loss
                    if (position.UnrealizedPnLPercent <= -(decimal)strategy.RiskConfig.StopLossPercent)
                        runner.Strategy.NotifyStopLossHit();

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

        // Persistir peak prices actualizados para trailing stop (BUG-1 fix)
        if (positionPriceChanged)
        {
            try
            {
                var unitOfWork = scope.ServiceProvider.GetRequiredService<Core.Interfaces.IUnitOfWork>();
                await unitOfWork.SaveChangesAsync(cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "Error al persistir peak prices de posiciones para '{Name}'. Se reintentará en el siguiente tick.",
                    runner.StrategyName);
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

    // ── Helpers ──────────────────────────────────────────────────────────────

    /// <summary>Duración en minutos de un intervalo de vela. Delega a la extensión centralizada.</summary>
    private static int GetIntervalMinutes(CandleInterval interval) => interval.ToMinutes();

    /// <summary>
    /// Calcula el número de períodos de warm-up necesarios para un indicador según su tipo.
    /// Delega a <see cref="IndicatorWarmUpHelper"/>.
    /// </summary>
    private static int GetIndicatorWarmUpPeriod(IndicatorConfig config) =>
        IndicatorWarmUpHelper.GetWarmUpPeriod(config);

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

        /// <summary>Config de estrategia cacheada. Se recarga solo en <see cref="ReloadStrategyAsync"/>.</summary>
        public TradingStrategy?           CachedConfig           { get; set; }

        public EmaAlignmentDetector       EmaAlignment           { get; } = new();
        public HigherHighLowDetector      HhLlDetector           { get; } = new();
        public MarketRegimeDetector       RegimeDetector         { get; } = new();
        public IndicatorTrackingState     TrackingState          { get; } = new();
        public MarketRegime               PreviousRegime         { get; set; } = MarketRegime.Unknown;
        public HigherHighLowDetector      HtfHhLlDetector        { get; } = new();

        // ── Pool v2 ──────────────────────────────────────────────────────
        public volatile bool              AllowNewEntries = true;
        public bool                       IsPoolRunner           { get; set; }
        public DateTimeOffset?            EnteredTopKAt          { get; set; }
        public TimeSpan                   MinTimeInTopKBeforeEntry { get; set; } = TimeSpan.FromSeconds(120);
        public string?                    BlockReason            { get; set; }
        public readonly object            PoolLock = new();

        // ── DESIGN-2: Caché de posiciones abiertas en memoria ────────────
        /// <summary>Posiciones abiertas cacheadas. Se invalida al abrir/cerrar posiciones.</summary>
        private IReadOnlyList<Position>? _cachedPositions;
        private DateTimeOffset _positionsCachedAt;
        /// <summary>TTL de la caché de posiciones: 2 segundos.</summary>
        private static readonly TimeSpan PositionCacheTtl = TimeSpan.FromSeconds(2);

        /// <summary>
        /// Obtiene posiciones abiertas de la caché si aún es válida, o las recarga de la DB.
        /// </summary>
        public async Task<IReadOnlyList<Position>> GetOpenPositionsAsync(
            IPositionRepository positionRepo,
            CancellationToken cancellationToken)
        {
            var now = DateTimeOffset.UtcNow;
            if (_cachedPositions is not null && now - _positionsCachedAt < PositionCacheTtl)
                return _cachedPositions;

            _cachedPositions = await positionRepo.GetOpenByStrategyIdAsync(StrategyId, cancellationToken);
            _positionsCachedAt = now;
            return _cachedPositions;
        }

        /// <summary>Invalida la caché de posiciones (llamar tras abrir/cerrar posición).</summary>
        public void InvalidatePositionCache() => _cachedPositions = null;

        public bool HasCachedOpenPositions => _cachedPositions?.Count > 0;

        /// <summary>Scope de larga vida que acompaña al runner. Se dispone con el runner.</summary>
        private readonly IServiceScope?   _scope;

        public StrategyRunnerState(
            Guid id, string name, Symbol symbol,
            ITradingStrategy strategy, CancellationTokenSource cts,
            IServiceScope? scope = null)
        {
            StrategyId              = id;
            StrategyName            = name;
            Symbol                  = symbol;
            Strategy                = strategy;
            CancellationTokenSource = cts;
            _scope                  = scope;
        }

        public void Cancel()
        {
            CancellationTokenSource.Cancel();
        }

        public void Dispose()
        {
            CancellationTokenSource.Dispose();
            _scope?.Dispose();
        }

        public StrategyEngineStatus ToStatus() => new(
            StrategyId, StrategyName, Symbol,
            IsProcessing, LastTickAt,
            TicksProcessed, SignalsGenerated, OrdersPlaced,
            Strategy.CurrentRegime,
            Strategy.IsBullish);
    }
}
