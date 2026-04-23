namespace TradingBot.Core.Enums;

/// <summary>
/// Naturaleza de la señal generada por un indicador.
/// Determina cómo los confirmadores evalúan la coherencia de la señal.
/// </summary>
public enum SignalNature
{
    /// <summary>Señal de seguimiento de tendencia (MACD cross, EMA/SMA cross).</summary>
    TrendFollowing,

    /// <summary>Señal de reversión a la media (RSI oversold/overbought, Bollinger bands).</summary>
    MeanReversion
}
