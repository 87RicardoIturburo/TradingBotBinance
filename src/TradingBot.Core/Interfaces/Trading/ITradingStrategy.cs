using TradingBot.Core.Common;
using TradingBot.Core.Entities;
using TradingBot.Core.Enums;
using TradingBot.Core.Events;
using TradingBot.Core.ValueObjects;

namespace TradingBot.Core.Interfaces.Trading;

/// <summary>
/// Contrato para todas las estrategias de trading.
/// Cada implementación encapsula su lógica de indicadores y generación de señales.
/// Registrada en DI para que el StrategyEngine la pueda resolver por <see cref="StrategyId"/>.
/// </summary>
public interface ITradingStrategy
{
    /// <summary>Id de la estrategia configurada que esta instancia ejecuta.</summary>
    Guid StrategyId { get; }

    /// <summary>Par de trading que procesa esta estrategia.</summary>
    Symbol Symbol { get; }

    /// <summary>Modo de operación actual (Live / Testnet / PaperTrading).</summary>
    TradingMode Mode { get; }

    /// <summary>Indica si la estrategia está lista para procesar ticks.</summary>
    bool IsInitialized { get; }

    /// <summary>
    /// Inicializa la estrategia con la configuración persistida.
    /// Llamado una vez al arrancar y en cada hot-reload.
    /// </summary>
    Task InitializeAsync(TradingStrategy config, CancellationToken cancellationToken = default);

    /// <summary>
    /// Procesa un tick de mercado y devuelve una señal si las condiciones se cumplen,
    /// o <c>null</c> si no hay señal en este tick.
    /// </summary>
    Task<Result<SignalGeneratedEvent?, DomainError>> ProcessTickAsync(
        MarketTickReceivedEvent tick,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Recarga la configuración en caliente sin detener el procesamiento.
    /// Publicado por <see cref="StrategyUpdatedEvent"/>.
    /// </summary>
    Task ReloadConfigAsync(TradingStrategy config, CancellationToken cancellationToken = default);

    /// <summary>Reinicia los buffers internos de todos los indicadores.</summary>
    void Reset();
}
