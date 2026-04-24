using TradingBot.Core.Enums;

namespace TradingBot.Application.AutoPilot;

/// <summary>
/// Resultado del scoring de un símbolo dentro del pool.
/// </summary>
public sealed record TradabilityEntry(
    string Symbol,
    decimal RawScore,
    decimal StabilityAdjustment,
    decimal FinalScore,
    decimal RegimeClarityNorm,
    decimal AdxStrengthNorm,
    decimal RelativeVolumeNorm,
    decimal AtrHealthNorm,
    decimal BandWidthNorm,
    decimal SignalProximityNorm);

/// <summary>
/// Datos necesarios del runner para calcular el TradabilityScore.
/// Proporcionados por <c>IStrategyEngine.GetPoolRunnerInfoAsync</c>.
/// </summary>
public sealed record PoolScoringData(
    string Symbol,
    MarketRegime Regime,
    decimal? AdxValue,
    decimal? VolumeRatio,
    decimal? AtrPercent,
    decimal? BandWidth,
    decimal SignalProximity,
    decimal RegimeStability);

/// <summary>
/// Calcula el TradabilityScore normalizado 0-100 para un símbolo.
/// Stateless: cada llamada a <see cref="Score"/> es independiente.
/// </summary>
public sealed class TradabilityScorer
{
    public TradabilityEntry Score(PoolScoringData data, SymbolPoolConfig config)
    {
        ArgumentNullException.ThrowIfNull(data);
        ArgumentNullException.ThrowIfNull(config);

        var regimeClarity = NormalizeRegimeClarity(data.Regime);
        var adxStrength = NormalizeAdxStrength(data.AdxValue);
        var relativeVolume = NormalizeRelativeVolume(data.VolumeRatio);
        var atrHealth = NormalizeAtrHealth(data.AtrPercent);
        var bandWidth = NormalizeBandWidth(data.BandWidth);
        var signalProximity = Math.Clamp(data.SignalProximity, 0m, 1m);

        var rawScore = (regimeClarity * config.RegimeClarityWeight
                      + adxStrength * config.AdxStrengthWeight
                      + relativeVolume * config.RelativeVolumeWeight
                      + atrHealth * config.AtrHealthWeight
                      + bandWidth * config.BandWidthWeight
                      + signalProximity * config.SignalProximityWeight) * 100m;

        var stabilityAdjustment = 0.7m + 0.3m * data.RegimeStability;
        var finalScore = rawScore * stabilityAdjustment;

        return new TradabilityEntry(
            data.Symbol,
            rawScore,
            stabilityAdjustment,
            finalScore,
            regimeClarity,
            adxStrength,
            relativeVolume,
            atrHealth,
            bandWidth,
            signalProximity);
    }

    private static decimal NormalizeRegimeClarity(MarketRegime regime) => regime switch
    {
        MarketRegime.Trending => 1.0m,
        MarketRegime.Ranging => 1.0m,
        MarketRegime.Bearish => 1.0m,
        MarketRegime.HighVolatility => 0.5m,
        MarketRegime.Unknown => 0.1m,
        MarketRegime.Indefinite => 0m,
        _ => 0m
    };

    private static decimal NormalizeAdxStrength(decimal? adx)
    {
        if (adx is null) return 0m;
        if (adx.Value <= 15m) return 0m;
        if (adx.Value >= 30m) return 1.0m;
        return (adx.Value - 15m) / 15m;
    }

    private static decimal NormalizeRelativeVolume(decimal? ratio)
    {
        if (ratio is null) return 0m;
        if (ratio.Value <= 0.5m) return 0m;
        if (ratio.Value >= 1.5m) return 1.0m;
        return (ratio.Value - 0.5m) / 1.0m;
    }

    private static decimal NormalizeAtrHealth(decimal? atrPct)
    {
        if (atrPct is null) return 0m;
        if (atrPct.Value < 0.5m) return 0m;
        if (atrPct.Value <= 2m) return (atrPct.Value - 0.5m) / 1.5m;
        if (atrPct.Value <= 5m) return 1m - (atrPct.Value - 2m) / 3m;
        return 0m;
    }

    private static decimal NormalizeBandWidth(decimal? bw)
    {
        if (bw is null) return 0m;
        if (bw.Value < 0.01m || bw.Value > 0.08m) return 0m;
        if (bw.Value >= 0.03m && bw.Value <= 0.05m) return 1.0m;
        if (bw.Value < 0.03m) return (bw.Value - 0.01m) / 0.02m;
        return 1.0m - (bw.Value - 0.05m) / 0.03m;
    }
}
