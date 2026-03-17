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
