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

    /// <summary>Régimen de mercado detectado en el último tick procesado.</summary>
    MarketRegime CurrentRegime { get; }

    /// <summary>Dirección de la tendencia actual según ADX (+DI &gt; -DI). <c>true</c> si no hay ADX.</summary>
    bool IsBullish { get; }

    /// <summary>Valor actual del ATR, o <c>null</c> si no está configurado o no está listo.</summary>
    decimal? CurrentAtrValue { get; }

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
    /// Procesa una vela cerrada: actualiza los indicadores técnicos y evalúa señales.
    /// Este es el método preferido para alimentar indicadores — produce señales
    /// consistentes y comparables con plataformas profesionales.
    /// </summary>
    Task<Result<SignalGeneratedEvent?, DomainError>> ProcessKlineAsync(
        KlineClosedEvent kline,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Procesa una vela cerrada del timeframe de confirmación (Multi-Timeframe Analysis).
    /// Actualiza el indicador de tendencia del HTF sin generar señales.
    /// </summary>
    void ProcessConfirmationKline(KlineClosedEvent kline);

    /// <summary>
    /// Indica si la tendencia del timeframe de confirmación está alineada
    /// con la dirección indicada. <c>true</c> si no hay confirmation timeframe configurado.
    /// </summary>
    bool IsConfirmationAligned(OrderSide side);

    /// <summary>
    /// EST-15: Procesa una vela cerrada de BTCUSDT para el filtro de correlación.
    /// Solo relevante para estrategias que operan altcoins (no BTC).
    /// </summary>
    void ProcessBtcKline(KlineClosedEvent kline);

    /// <summary>
    /// EST-15: Indica si la tendencia de BTC está alineada con la dirección indicada.
    /// Para Buy: BTC debe estar por encima de su EMA. <c>true</c> si el filtro no aplica
    /// (la estrategia opera BTCUSDT) o no está listo.
    /// </summary>
    bool IsBtcAligned(OrderSide side);

    /// <summary>
    /// Recarga la configuración en caliente sin detener el procesamiento.
    /// Publicado por <see cref="StrategyUpdatedEvent"/>.
    /// </summary>
    Task ReloadConfigAsync(TradingStrategy config, CancellationToken cancellationToken = default);

    /// <summary>Reinicia los buffers internos de todos los indicadores.</summary>
    void Reset();

    /// <summary>
    /// EST-17: Notifica a la estrategia que una posición fue cerrada por stop-loss.
    /// Permite activar el modo de re-entrada con cooldown reducido si la tendencia sigue intacta.
    /// </summary>
    void NotifyStopLossHit();

    /// <summary>
    /// Alimenta un precio a los indicadores sin evaluar señales ni afectar
    /// el estado de trading (cooldown, cruce previo, régimen).
    /// Usado durante warm-up de backtesting y optimización.
    /// </summary>
    void WarmUpPrice(decimal price);

    /// <summary>
    /// Alimenta datos OHLC a los indicadores sin evaluar señales.
    /// Preferido sobre <see cref="WarmUpPrice"/> cuando se dispone de velas completas,
    /// ya que permite calcular ATR con True Range real en vez de aproximaciones.
    /// </summary>
    void WarmUpOhlc(decimal high, decimal low, decimal close, decimal volume = 0m);

    /// <summary>
    /// Devuelve un snapshot de los valores actuales de todos los indicadores listos,
    /// en el formato "RSI(14)=28.5000 | EMA(12)=50100.0000".
    /// Usado por el RuleEngine para evaluar condiciones de reglas de salida.
    /// </summary>
    string GetCurrentSnapshot();
}
