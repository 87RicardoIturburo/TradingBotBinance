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
