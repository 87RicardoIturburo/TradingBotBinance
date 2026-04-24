using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TradingBot.Core.Enums;
using TradingBot.Core.Events;
using TradingBot.Core.Interfaces;
using TradingBot.Core.Interfaces.Repositories;
using TradingBot.Core.Interfaces.Services;

namespace TradingBot.Application.AutoPilot;

/// <summary>
/// BackgroundService que gestiona el pool dinámico de símbolos (AutoPilot v2).
/// Ciclo cada <see cref="SymbolPoolConfig.EvaluationIntervalSeconds"/>s:
/// universo → reconciliar → score → histéresis → Top K → SetAllowNewEntries → cleanup → métricas.
/// </summary>
internal sealed class SymbolPoolManager : BackgroundService, ISymbolPool
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IStrategyEngine _engine;
    private readonly IOptionsMonitor<SymbolPoolConfig> _configMonitor;
    private readonly TradabilityScorer _scorer;
    private readonly ILogger<SymbolPoolManager> _logger;

    private volatile bool _enabled;
    private readonly object _stateLock = new();

    private readonly Dictionary<string, int> _cyclesInTopK = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, int> _cyclesOutOfTopK = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _confirmedTopK = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _observedSymbols = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, TradabilityEntry> _lastScores = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, DateTimeOffset> _excludedSymbols = new(StringComparer.OrdinalIgnoreCase);

    private TaskCompletionSource _forceRefreshTcs = new();

    public SymbolPoolManager(
        IServiceScopeFactory scopeFactory,
        IStrategyEngine engine,
        IOptionsMonitor<SymbolPoolConfig> configMonitor,
        TradabilityScorer scorer,
        ILogger<SymbolPoolManager> logger)
    {
        _scopeFactory = scopeFactory;
        _engine = engine;
        _configMonitor = configMonitor;
        _scorer = scorer;
        _logger = logger;
        _enabled = configMonitor.CurrentValue.Enabled;
    }

    // ── ISymbolPool ─────────────────────────────────────────────────────────

    public Task<IReadOnlyList<string>> GetObservedSymbolsAsync(CancellationToken ct = default)
    {
        lock (_stateLock)
            return Task.FromResult<IReadOnlyList<string>>([.. _observedSymbols]);
    }

    public Task<IReadOnlyList<string>> GetActiveSymbolsAsync(CancellationToken ct = default)
    {
        lock (_stateLock)
            return Task.FromResult<IReadOnlyList<string>>([.. _confirmedTopK]);
    }

    public async Task<IReadOnlyList<SymbolTradabilityInfo>> GetTradabilityScoresAsync(CancellationToken ct = default)
    {
        var infos = await _engine.GetAllPoolRunnerInfosAsync(ct);
        lock (_stateLock)
        {
            return infos.Select(i =>
            {
                _lastScores.TryGetValue(i.Symbol, out var entry);
                return new SymbolTradabilityInfo(
                    i.Symbol,
                    entry?.FinalScore ?? 0m,
                    i.Regime.ToString(),
                    _confirmedTopK.Contains(i.Symbol),
                    i.AllowNewEntries,
                    i.BlockReason,
                    i.RegimeStability);
            }).ToList();
        }
    }

    public Task<bool> IsEnabledAsync(CancellationToken ct = default) =>
        Task.FromResult(_enabled);

    public async Task SetEnabledAsync(bool enabled, CancellationToken ct = default)
    {
        _enabled = enabled;
        _logger.LogInformation("SymbolPool {State} desde API", enabled ? "ACTIVADO" : "DESACTIVADO");

        if (!enabled)
            await GracefulShutdownPoolAsync(ct);
    }

    public Task ForceRefreshAsync(CancellationToken ct = default)
    {
        var old = _forceRefreshTcs;
        _forceRefreshTcs = new TaskCompletionSource();
        old.TrySetResult();
        return Task.CompletedTask;
    }

    public Task ExcludeSymbolAsync(string symbol, TimeSpan duration, CancellationToken ct = default)
    {
        var until = DateTimeOffset.UtcNow.Add(duration);
        _excludedSymbols[symbol.ToUpperInvariant()] = until;
        _logger.LogInformation("SymbolPool: {Symbol} excluido hasta {Until}", symbol, until);
        return Task.CompletedTask;
    }

    // ── BackgroundService ───────────────────────────────────────────────────

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("SymbolPoolManager arrancando…");

        await ResetPoolStrategiesOnStartupAsync(stoppingToken);

        await Task.Delay(TimeSpan.FromSeconds(15), stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            var config = _configMonitor.CurrentValue;
            var interval = TimeSpan.FromSeconds(config.EvaluationIntervalSeconds);

            try
            {
                if (_enabled)
                    await RunCycleAsync(config, stoppingToken);
                else
                    _logger.LogDebug("SymbolPool deshabilitado, ciclo omitido");
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error en ciclo del SymbolPoolManager");
            }

            var delayTask = Task.Delay(interval, stoppingToken);
            await Task.WhenAny(delayTask, _forceRefreshTcs.Task);
        }
    }

    private async Task ResetPoolStrategiesOnStartupAsync(CancellationToken ct)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var repo = scope.ServiceProvider.GetRequiredService<IStrategyRepository>();
            var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
            var poolStrategies = await repo.GetByOriginAsync(StrategyOrigin.Pool, ct);
            var deactivated = 0;

            foreach (var strategy in poolStrategies)
            {
                if (!strategy.IsActive) continue;
                strategy.Deactivate();
                await repo.UpdateAsync(strategy, ct);
                deactivated++;
            }

            if (deactivated > 0)
            {
                await unitOfWork.SaveChangesAsync(ct);
                _logger.LogInformation(
                    "SymbolPool: {Count} estrategias Pool desactivadas al arrancar (stateless rebuild)",
                    deactivated);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "No se pudieron resetear estrategias Pool al arrancar");
        }
    }

    private async Task GracefulShutdownPoolAsync(CancellationToken ct)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var repo = scope.ServiceProvider.GetRequiredService<IStrategyRepository>();
            var positionRepo = scope.ServiceProvider.GetRequiredService<IPositionRepository>();
            var poolStrategies = await repo.GetByOriginAsync(StrategyOrigin.Pool, ct);

            foreach (var strategy in poolStrategies.Where(s => s.IsActive))
            {
                var openPositions = await positionRepo.GetOpenByStrategyIdAsync(strategy.Id, ct);

                if (openPositions.Count == 0)
                {
                    await _engine.StopPoolRunnerAsync(strategy.Symbol.Value, ct);
                }
                else
                {
                    await _engine.SetAllowNewEntriesAsync(
                        strategy.Symbol.Value, false, "PoolDisabled", ct);
                    _logger.LogInformation(
                        "Pool desactivado: {Symbol} mantiene runner por {Count} posiciones abiertas",
                        strategy.Symbol.Value, openPositions.Count);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error durante apagado graceful del pool");
        }
    }

    private async Task RunCycleAsync(SymbolPoolConfig config, CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var scanner = scope.ServiceProvider.GetRequiredService<IMarketScanner>();
        var notifier = scope.ServiceProvider.GetService<ITradingNotifier>();

        // 1. Actualizar universo desde scanner
        var scanResult = await scanner.ScanAsync(config.ObservedPoolSize, ct);
        if (scanResult.IsFailure)
        {
            _logger.LogWarning("Scanner falló: {Error}", scanResult.Error.Message);
            return;
        }

        var candidates = scanResult.Value
            .OrderByDescending(s => s.Score)
            .Take(config.ObservedPoolSize)
            .ToList();

        lock (_stateLock)
        {
            _observedSymbols.Clear();
            foreach (var c in candidates)
                _observedSymbols.Add(c.Symbol);
        }

        // 2. Reconciliar runners (start/stop, respetar MaxConcurrentRunners)
        await ReconcileRunnersAsync(candidates, config, ct);

        // 3. TradabilityScore (snapshot atómico + normalización 0-1 + regime stability suave)
        var runnerInfos = await _engine.GetAllPoolRunnerInfosAsync(ct);
        var scored = new List<(PoolRunnerInfo Info, TradabilityEntry Entry)>();

        foreach (var info in runnerInfos)
        {
            var scoringData = new PoolScoringData(
                info.Symbol, info.Regime, info.AdxValue, info.VolumeRatio,
                info.AtrPercent, info.BandWidth, info.SignalProximity, info.RegimeStability);
            var entry = _scorer.Score(scoringData, config);
            scored.Add((info, entry));

            lock (_stateLock)
                _lastScores[info.Symbol] = entry;
        }

        // 4. Histéresis contadores ciclos
        var aboveThreshold = scored
            .Where(s => s.Entry.FinalScore >= config.MinTradabilityScore)
            .OrderByDescending(s => s.Entry.FinalScore)
            .Take(config.ActiveTopK)
            .Select(s => s.Info.Symbol)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var blockedByRegime = 0;
        var blockedByScore = 0;
        var blockedByCooldown = 0;

        foreach (var s in scored)
        {
            var sym = s.Info.Symbol;
            var inCandidate = aboveThreshold.Contains(sym);

            if (inCandidate)
            {
                _cyclesInTopK.TryGetValue(sym, out var inCount);
                _cyclesInTopK[sym] = inCount + 1;
                _cyclesOutOfTopK[sym] = 0;
            }
            else
            {
                _cyclesOutOfTopK.TryGetValue(sym, out var outCount);
                _cyclesOutOfTopK[sym] = outCount + 1;
                _cyclesInTopK[sym] = 0;
            }
        }

        // 5. Top K = score > MinTradabilityScore + histéresis confirmada (vacío = no operar)
        var newTopK = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var sym in aboveThreshold)
        {
            _cyclesInTopK.TryGetValue(sym, out var inCount);
            if (inCount >= config.MinCyclesInTopK)
                newTopK.Add(sym);
            else
                blockedByCooldown++;
        }

        // Mantener los que estaban si no han acumulado suficientes ciclos fuera
        lock (_stateLock)
        {
            foreach (var sym in _confirmedTopK)
            {
                _cyclesOutOfTopK.TryGetValue(sym, out var outCount);
                if (outCount < config.MinCyclesOutOfTopK)
                    newTopK.Add(sym);
            }
        }

        // Contar bloqueos
        foreach (var s in scored)
        {
            if (s.Info.Regime is Core.Enums.MarketRegime.Indefinite or Core.Enums.MarketRegime.Unknown)
                blockedByRegime++;
            else if (s.Entry.FinalScore < config.MinTradabilityScore && !newTopK.Contains(s.Info.Symbol))
                blockedByScore++;
        }

        // 6. SetAllowNewEntries + BlockReason
        foreach (var s in scored)
        {
            var sym = s.Info.Symbol;
            var isActive = newTopK.Contains(sym);
            string? blockReason = null;

            if (!isActive)
            {
                blockReason = s.Info.Regime switch
                {
                    Core.Enums.MarketRegime.Indefinite => "IndefiniteRegime",
                    Core.Enums.MarketRegime.Unknown => "UnknownRegime",
                    _ when s.Entry.FinalScore < config.MinTradabilityScore => "BelowScoreThreshold",
                    _ => "NotInTopK"
                };

                _cyclesInTopK.TryGetValue(sym, out var inCount);
                if (inCount > 0 && inCount < config.MinCyclesInTopK)
                    blockReason = "Cooldown";
            }

            await _engine.SetAllowNewEntriesAsync(sym, isActive, blockReason, ct);
        }

        // 7. EnteredTopKAt transiciones — gestionado automáticamente en SetAllowNewEntriesAsync

        // 8. Cleanup zombies
        var zombiesRemoved = 0;
        foreach (var s in scored)
        {
            var sym = s.Info.Symbol;
            if (newTopK.Contains(sym)) continue;
            if (s.Info.HasOpenPosition) continue;

            var idle = DateTimeOffset.UtcNow - s.Info.LastActivityAt;
            if (idle > TimeSpan.FromMinutes(config.IdleTimeoutMinutes)
                && s.Entry.FinalScore < config.ZombieScoreThreshold
                && !_observedSymbols.Contains(sym))
            {
                await _engine.StopPoolRunnerAsync(sym, ct);
                zombiesRemoved++;
                _logger.LogInformation("Zombie runner eliminado: {Symbol} (score={Score:F1}, idle={Idle}min)",
                    sym, s.Entry.FinalScore, idle.TotalMinutes);
            }
        }

        // Actualizar estado confirmado
        lock (_stateLock)
        {
            _confirmedTopK.Clear();
            foreach (var sym in newTopK)
                _confirmedTopK.Add(sym);
        }

        // 9. Publicar métricas de bloqueo
        _logger.LogInformation(
            "SymbolPool ciclo: {Evaluated} evaluados, {BlockedByRegime} bloqueados por régimen, " +
            "{BlockedByScore} por score < {MinScore}, {BlockedByCooldown} por cooldown, " +
            "{Active} activos en Top K, {Zombies} runners eliminados",
            scored.Count, blockedByRegime, blockedByScore, config.MinTradabilityScore,
            blockedByCooldown, newTopK.Count, zombiesRemoved);

        var snapshot = new SymbolPoolSnapshot(
            _enabled,
            scored.Count,
            blockedByRegime,
            blockedByScore,
            blockedByCooldown,
            newTopK.Count,
            zombiesRemoved,
            scored.Select(s => new SymbolPoolItemSnapshot(
                s.Info.Symbol,
                s.Entry.FinalScore,
                s.Info.Regime.ToString(),
                newTopK.Contains(s.Info.Symbol),
                s.Info.AllowNewEntries,
                s.Info.BlockReason,
                s.Info.RegimeStability)).ToList(),
            DateTimeOffset.UtcNow);

        if (notifier is not null)
            await notifier.NotifySymbolPoolUpdateAsync(snapshot, ct);
    }

    private async Task ReconcileRunnersAsync(
        IReadOnlyList<SymbolScore> candidates, SymbolPoolConfig config, CancellationToken ct)
    {
        var templateId = Guid.Empty;
        if (!string.IsNullOrWhiteSpace(config.BaseTemplateId)
            && Guid.TryParse(config.BaseTemplateId, out var parsed))
        {
            templateId = parsed;
        }
        else
        {
            _logger.LogDebug("BaseTemplateId no configurado, el engine usará DefaultPoolTemplateFactory");
        }

        var currentRunners = await _engine.GetAllPoolRunnerInfosAsync(ct);
        var currentSymbols = currentRunners.Select(r => r.Symbol).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var desiredSymbols = candidates.Select(c => c.Symbol).ToHashSet(StringComparer.OrdinalIgnoreCase);

        // Stop runners que ya no están en el universo observado (y no tienen posición abierta)
        foreach (var runner in currentRunners)
        {
            if (!desiredSymbols.Contains(runner.Symbol) && !runner.HasOpenPosition)
            {
                await _engine.StopPoolRunnerAsync(runner.Symbol, ct);
                _logger.LogDebug("Runner removido del pool: {Symbol}", runner.Symbol);
            }
        }

        // Start runners nuevos (respetando cap y blacklist)
        var now = DateTimeOffset.UtcNow;
        var activeCount = (await _engine.GetAllPoolRunnerInfosAsync(ct)).Count;
        foreach (var candidate in candidates)
        {
            if (activeCount >= config.MaxConcurrentRunners) break;
            if (currentSymbols.Contains(candidate.Symbol)) continue;

            if (_excludedSymbols.TryGetValue(candidate.Symbol, out var excludedUntil))
            {
                if (now < excludedUntil)
                    continue;
                _excludedSymbols.TryRemove(candidate.Symbol, out _);
            }

            var result = await _engine.StartPoolRunnerAsync(
                candidate.Symbol, templateId, config.DefaultTimeframe, config.DefaultTradingMode, ct);

            if (result.IsSuccess)
                activeCount++;
            else
                _logger.LogWarning("No se pudo crear runner para {Symbol}: {Error}",
                    candidate.Symbol, result.Error.Message);
        }
    }
}
