namespace TradingBot.Core.Enums;

/// <summary>
/// Régimen de mercado detectado. Determina qué tipo de señales son más fiables.
/// </summary>
public enum MarketRegime
{
    /// <summary>Datos insuficientes para clasificar.</summary>
    Unknown,

    /// <summary>Mercado lateral, oscilando entre soporte y resistencia. RSI funciona bien.</summary>
    Ranging,

    /// <summary>Tendencia alcista o bajista clara. Seguir la tendencia, no operar contra ella.</summary>
    Trending,

    /// <summary>Volatilidad extrema. Reducir tamaño de posición o pausar.</summary>
    HighVolatility
}
