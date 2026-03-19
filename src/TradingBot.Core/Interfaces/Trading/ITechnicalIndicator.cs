using TradingBot.Core.Enums;

namespace TradingBot.Core.Interfaces.Trading;

/// <summary>
/// Contrato para todos los indicadores técnicos (RSI, MACD, EMA, SMA, Bollinger…).
/// Cada implementación mantiene su propio buffer de datos y estado interno.
/// </summary>
public interface ITechnicalIndicator
{
    /// <summary>Tipo de indicador implementado.</summary>
    IndicatorType Type { get; }

    /// <summary>Nombre legible. Ej: "RSI(14)".</summary>
    string Name { get; }

    /// <summary>
    /// Indica si el indicador tiene suficientes datos para calcular un valor fiable.
    /// Mientras sea <c>false</c>, <see cref="Calculate"/> devuelve <c>null</c>.
    /// </summary>
    bool IsReady { get; }

    /// <summary>Alimenta un nuevo valor de precio al buffer del indicador.</summary>
    /// <param name="value">Precio de cierre del período (o cualquier serie temporal).</param>
    void Update(decimal value);

    /// <summary>
    /// Calcula y devuelve el valor actual del indicador.
    /// Devuelve <c>null</c> si <see cref="IsReady"/> es <c>false</c>.
    /// </summary>
    decimal? Calculate();

    /// <summary>Limpia el buffer interno y reinicia el estado del indicador.</summary>
    void Reset();

    /// <summary>
    /// Serializa el estado interno del indicador a JSON para persistencia en Redis.
    /// Permite restaurar el indicador sin warm-up tras un reinicio.
    /// </summary>
    string SerializeState();

    /// <summary>
    /// Restaura el estado interno desde JSON previamente serializado con <see cref="SerializeState"/>.
    /// </summary>
    /// <returns><c>true</c> si la restauración fue exitosa; <c>false</c> si el JSON es inválido.</returns>
    bool DeserializeState(string json);
}

/// <summary>
/// Indicador que puede recibir datos OHLC completos (High, Low, Close) para un cálculo preciso.
/// Implementado por indicadores que se benefician de velas completas (ATR, ADX).
/// Cuando se dispone de datos OHLC, llamar <see cref="UpdateOhlc"/> en vez de <see cref="ITechnicalIndicator.Update"/>.
/// </summary>
public interface IOhlcIndicator
{
    /// <summary>
    /// Alimenta una vela completa al indicador.
    /// Cuando este método está disponible, tiene prioridad sobre <see cref="ITechnicalIndicator.Update"/>.
    /// </summary>
    void UpdateOhlc(decimal high, decimal low, decimal close);
}

/// <summary>
/// Indicador que se alimenta con datos de volumen en vez de precio.
/// Implementado por indicadores como Volume SMA que analizan el volumen de trading.
/// </summary>
public interface IVolumeIndicator
{
    /// <summary>Alimenta un valor de volumen al buffer del indicador.</summary>
    void UpdateVolume(decimal volume);
}
