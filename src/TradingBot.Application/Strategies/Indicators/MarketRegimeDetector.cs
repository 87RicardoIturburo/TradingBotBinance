using TradingBot.Core.Enums;

namespace TradingBot.Application.Strategies.Indicators;

/// <summary>
/// Clasifica el régimen de mercado actual combinando ADX, Bollinger BandWidth y ATR.
/// <list type="bullet">
///   <item><see cref="MarketRegime.Trending"/> — ADX &gt; 25</item>
///   <item><see cref="MarketRegime.Ranging"/> — ADX &lt; 20</item>
///   <item><see cref="MarketRegime.HighVolatility"/> — BandWidth &gt; umbral o ATR &gt; umbral relativo</item>
///   <item><see cref="MarketRegime.Unknown"/> — indicadores no listos</item>
/// </list>
/// </summary>
internal sealed class MarketRegimeDetector
{
    /// <summary>
    /// Detecta el régimen actual basándose en los indicadores disponibles.
    /// Los umbrales son configurables por estrategia vía <see cref="RiskConfig"/>.
    /// </summary>
    public static MarketRegimeResult Detect(
        AdxIndicator? adx,
        BollingerBandsIndicator? bollinger,
        AtrIndicator? atr,
        decimal currentPrice,
        decimal adxTrendingThreshold = 25m,
        decimal adxRangingThreshold = 20m,
        decimal highVolatilityBandWidthPercent = 0.08m,
        decimal highVolatilityAtrPercent = 0.03m)
    {
        // Si no hay ningún indicador disponible, no podemos clasificar
        if (adx is null or { IsReady: false }
            && bollinger is null or { IsReady: false }
            && atr is null or { IsReady: false })
        {
            return new MarketRegimeResult(MarketRegime.Unknown, null, null, null);
        }

        var adxValue = adx is { IsReady: true } ? adx.Adx : null;
        var bandWidth = bollinger is { IsReady: true } ? bollinger.BandWidth : null;
        var atrValue = atr is { IsReady: true } ? atr.Value : null;

        // ATR relativo al precio (normalizado)
        decimal? atrPercent = atrValue.HasValue && currentPrice > 0
            ? atrValue.Value / currentPrice
            : null;

        // Alta volatilidad tiene prioridad
        if (bandWidth > highVolatilityBandWidthPercent
            || (atrPercent.HasValue && atrPercent.Value > highVolatilityAtrPercent))
        {
            return new MarketRegimeResult(MarketRegime.HighVolatility, adxValue, bandWidth, atrPercent);
        }

        // Trending vs Ranging basado en ADX
        if (adxValue.HasValue)
        {
            if (adxValue.Value >= adxTrendingThreshold)
                return new MarketRegimeResult(MarketRegime.Trending, adxValue, bandWidth, atrPercent);

            if (adxValue.Value <= adxRangingThreshold)
                return new MarketRegimeResult(MarketRegime.Ranging, adxValue, bandWidth, atrPercent);
        }

        // ADX entre ranging-trending: zona ambigua — usar BandWidth como desempate
        if (bandWidth.HasValue)
        {
            return bandWidth.Value < 0.04m
                ? new MarketRegimeResult(MarketRegime.Ranging, adxValue, bandWidth, atrPercent)
                : new MarketRegimeResult(MarketRegime.Trending, adxValue, bandWidth, atrPercent);
        }

        return new MarketRegimeResult(MarketRegime.Unknown, adxValue, bandWidth, atrPercent);
    }
}

/// <summary>Resultado de la detección de régimen con métricas de soporte.</summary>
internal sealed record MarketRegimeResult(
    MarketRegime Regime,
    decimal? AdxValue,
    decimal? BandWidth,
    decimal? AtrPercent);
