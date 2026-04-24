using TradingBot.Core.Common;
using TradingBot.Core.Entities;
using TradingBot.Core.Enums;
using TradingBot.Core.ValueObjects;

namespace TradingBot.Core.Interfaces.Services;

/// <summary>
/// Motor de estrategias. Orquesta el flujo completo:
/// MarketTick → ITradingStrategy → IRuleEngine → IRiskManager → IOrderService.
/// Se ejecuta como servicio en background (<c>IHostedService</c>).
/// </summary>
public interface IStrategyEngine
{
    /// <summary>Indica si el motor está procesando ticks activamente.</summary>
    bool IsRunning { get; }

    /// <summary>
    /// Arranca el motor y comienza a procesar ticks de todas las estrategias activas.
    /// </summary>
    Task StartAsync(CancellationToken cancellationToken = default);

    /// <summary>Detiene el procesamiento de forma ordenada.</summary>
    Task StopAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Pausa temporalmente el motor (no cancela posiciones abiertas).
    /// </summary>
    Task PauseAsync(CancellationToken cancellationToken = default);

    /// <summary>Reanuda el motor tras una pausa.</summary>
    Task ResumeAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Recarga en caliente la configuración de una estrategia activa.
    /// Llamado por el <c>StrategyConfigService</c> al recibir <c>StrategyUpdatedEvent</c>.
    /// </summary>
    Task ReloadStrategyAsync(Guid strategyId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Devuelve el estado de procesamiento de todas las estrategias activas.
    /// Usado por el endpoint <c>/api/system/status</c>.
    /// </summary>
    Task<IReadOnlyDictionary<Guid, StrategyEngineStatus>> GetStatusAsync(
        CancellationToken cancellationToken = default);

    // ── Pool v2: gestión de runners de pool dinámico ─────────────────────

    /// <summary>Arranca un runner de pool para el símbolo dado usando el template base.</summary>
    Task<Result<Guid, DomainError>> StartPoolRunnerAsync(
        string symbol, Guid templateId, string timeframe, string tradingMode,
        CancellationToken ct = default);

    /// <summary>Detiene y elimina un runner de pool.</summary>
    Task StopPoolRunnerAsync(string symbol, CancellationToken ct = default);

    /// <summary>Controla si un runner de pool puede abrir nuevas entradas.</summary>
    Task SetAllowNewEntriesAsync(string symbol, bool allow, string? blockReason, CancellationToken ct = default);

    /// <summary>Obtiene un snapshot atómico de los datos de scoring de un runner de pool.</summary>
    Task<PoolRunnerInfo?> GetPoolRunnerInfoAsync(string symbol, CancellationToken ct = default);

    /// <summary>Obtiene snapshots de todos los runners de pool activos.</summary>
    Task<IReadOnlyList<PoolRunnerInfo>> GetAllPoolRunnerInfosAsync(CancellationToken ct = default);
}

/// <summary>Snapshot del estado de una estrategia dentro del motor.</summary>
public sealed record StrategyEngineStatus(
    Guid           StrategyId,
    string         StrategyName,
    Symbol         Symbol,
    bool           IsProcessing,
    DateTimeOffset LastTickAt,
    int            TicksProcessed,
    int            SignalsGenerated,
    int            OrdersPlaced,
    MarketRegime   CurrentRegime = MarketRegime.Unknown,
    bool           IsBullish = true);

/// <summary>
/// Snapshot atómico de un runner de pool para scoring.
/// Incluye todos los datos necesarios para calcular el TradabilityScore.
/// </summary>
public sealed record PoolRunnerInfo(
    string Symbol,
    Guid StrategyId,
    MarketRegime Regime,
    bool IsBullish,
    decimal? AdxValue,
    decimal? VolumeRatio,
    decimal? AtrPercent,
    decimal? BandWidth,
    decimal SignalProximity,
    decimal RegimeStability,
    bool AllowNewEntries,
    bool IsPoolRunner,
    DateTimeOffset? EnteredTopKAt,
    string? BlockReason,
    bool HasOpenPosition,
    DateTimeOffset LastActivityAt);
