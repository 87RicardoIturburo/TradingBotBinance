using TradingBot.Core.Enums;

namespace TradingBot.Application.Strategies.Indicators;

/// <summary>
/// Clasifica el régimen de mercado usando scoring híbrido:
/// ADX (fuerza) + EMA alignment (dirección) + HH/HL (estructura) + Bollinger (rango) + Volumen (validación).
/// <para>Pipeline: Scoring → Confirmación (N velas) → Histéresis.</para>
/// </summary>
internal sealed class MarketRegimeDetector
{
    private readonly Queue<MarketRegime> _recentRegimes = new();
    private MarketRegime _confirmedRegime = MarketRegime.Unknown;

    /// <summary>
    /// Scoring híbrido de régimen. Combina indicadores clásicos con EMA alignment, HH/HL y volumen.
    /// </summary>
    public MarketRegimeResult Detect(
        AdxIndicator? adx,
        BollingerBandsIndicator? bollinger,
        AtrIndicator? atr,
        decimal currentPrice,
        decimal adxTrendingThreshold = 25m,
        decimal adxRangingThreshold = 20m,
        decimal highVolatilityBandWidthPercent = 0.08m,
        decimal highVolatilityAtrPercent = 0.03m,
        EmaAlignmentDetector? emaAlignment = null,
        HigherHighLowDetector? hhLlDetector = null,
        decimal? volumeRatio = null,
        decimal indefiniteAdxThreshold = 15m)
    {
        var hasClassicIndicators =
            adx is { IsReady: true }
            || bollinger is { IsReady: true }
            || atr is { IsReady: true };

        var hasNewDetectors =
            emaAlignment is { IsReady: true }
            || (hhLlDetector is not null && hhLlDetector.IsReady())
            || volumeRatio.HasValue;

        if (!hasClassicIndicators && !hasNewDetectors)
        {
            return new MarketRegimeResult(MarketRegime.Unknown, null, null, null, null);
        }

        var adxValue = adx is { IsReady: true } ? adx.Adx : null;
        var bandWidth = bollinger is { IsReady: true } ? bollinger.BandWidth : null;
        var atrValue = atr is { IsReady: true } ? atr.Value : null;

        decimal? atrPercent = atrValue.HasValue && currentPrice > 0
            ? atrValue.Value / currentPrice
            : null;

        if (bandWidth > highVolatilityBandWidthPercent
            || (atrPercent.HasValue && atrPercent.Value > highVolatilityAtrPercent))
        {
            var hvScore = new MarketRegimeScore(0, 0, 0, MarketRegime.HighVolatility);
            return new MarketRegimeResult(MarketRegime.HighVolatility, adxValue, bandWidth, atrPercent, hvScore);
        }

        var trendingScore = 0;
        var rangingScore = 0;
        var indefiniteScore = 0;

        if (adxValue.HasValue && adxValue.Value >= adxTrendingThreshold)
            trendingScore++;

        if (adxValue.HasValue && adxValue.Value <= adxRangingThreshold)
            rangingScore++;

        if (emaAlignment is { IsReady: true })
        {
            if (emaAlignment.IsBullishAligned || emaAlignment.IsBearishAligned)
                trendingScore++;
            else if (emaAlignment.IsFlat())
                rangingScore++;
            else
                indefiniteScore++;
        }

        if (hhLlDetector is not null && hhLlDetector.IsReady())
        {
            if (hhLlDetector.HasHigherHighs() || hhLlDetector.HasLowerLows())
                trendingScore++;
        }

        if (volumeRatio.HasValue)
        {
            if (volumeRatio.Value >= 1.0m)
                trendingScore++;
            if (volumeRatio.Value < 0.5m)
                indefiniteScore++;
        }

        if (bandWidth.HasValue && bandWidth.Value < 0.04m)
            rangingScore++;

        if (adxValue.HasValue && adxValue.Value < indefiniteAdxThreshold)
            indefiniteScore++;

        var maxScore = Math.Max(trendingScore, Math.Max(rangingScore, indefiniteScore));

        MarketRegime winner;

        if (maxScore < 2)
            winner = MarketRegime.Indefinite;
        else if (indefiniteScore >= 2)
            winner = MarketRegime.Indefinite;
        else if (trendingScore > rangingScore)
            winner = MarketRegime.Trending;
        else if (rangingScore > trendingScore)
            winner = MarketRegime.Ranging;
        else
            winner = adxValue.HasValue && adxValue.Value >= adxTrendingThreshold
                ? MarketRegime.Trending
                : MarketRegime.Ranging;

        if (winner == MarketRegime.Trending
            && adx is { IsReady: true, IsBearish: true }
            && adx.MinusDi - adx.PlusDi > 5m)
        {
            winner = MarketRegime.Bearish;
        }

        var score = new MarketRegimeScore(trendingScore, rangingScore, indefiniteScore, winner);
        return new MarketRegimeResult(winner, adxValue, bandWidth, atrPercent, score);
    }

    /// <summary>
    /// Confirmación bidireccional: el régimen solo cambia si N velas consecutivas coinciden.
    /// </summary>
    public MarketRegime GetConfirmedRegime(MarketRegime detected, int confirmationCandles)
    {
        _recentRegimes.Enqueue(detected);
        while (_recentRegimes.Count > confirmationCandles)
            _recentRegimes.Dequeue();

        if (_recentRegimes.Count >= confirmationCandles
            && _recentRegimes.All(r => r == detected))
        {
            _confirmedRegime = detected;
        }

        return _confirmedRegime;
    }

    /// <summary>
    /// Histéresis: evita ping-pong Trending↔Ranging en zona ambigua del ADX.
    /// </summary>
    public static MarketRegimeResult ApplyHysteresis(
        MarketRegimeResult detected,
        MarketRegime previousRegime,
        decimal adxTrendingThreshold,
        decimal adxRangingThreshold)
    {
        const decimal HysteresisPoints = 2m;

        if (detected.AdxValue is null
            || detected.Regime is MarketRegime.HighVolatility or MarketRegime.Unknown or MarketRegime.Indefinite)
            return detected;

        if (previousRegime == MarketRegime.Bearish && detected.Regime != MarketRegime.Bearish)
        {
            if (detected.AdxValue.Value > adxTrendingThreshold - HysteresisPoints)
                return detected with { Regime = MarketRegime.Bearish };
        }

        if (previousRegime is MarketRegime.Trending or MarketRegime.Ranging
            && detected.Regime == MarketRegime.Bearish)
        {
            if (detected.AdxValue.Value < adxTrendingThreshold + HysteresisPoints)
                return detected with { Regime = previousRegime };
        }

        if (previousRegime == MarketRegime.Trending && detected.Regime == MarketRegime.Ranging)
        {
            if (detected.AdxValue.Value > adxRangingThreshold - HysteresisPoints)
                return detected with { Regime = MarketRegime.Trending };
        }
        else if (previousRegime == MarketRegime.Ranging && detected.Regime == MarketRegime.Trending)
        {
            if (detected.AdxValue.Value < adxTrendingThreshold + HysteresisPoints)
                return detected with { Regime = MarketRegime.Ranging };
        }

        return detected;
    }

    public void ResetConfirmation()
    {
        _recentRegimes.Clear();
        _confirmedRegime = MarketRegime.Unknown;
    }
}

/// <summary>Resultado del scoring por régimen.</summary>
internal sealed record MarketRegimeScore(
    int TrendingScore,
    int RangingScore,
    int IndefiniteScore,
    MarketRegime WinningRegime);

/// <summary>Resultado de la detección de régimen con métricas de soporte.</summary>
internal sealed record MarketRegimeResult(
    MarketRegime Regime,
    decimal? AdxValue,
    decimal? BandWidth,
    decimal? AtrPercent,
    MarketRegimeScore? Score);
