using TradingBot.Core.Interfaces.Services;

namespace TradingBot.Application.Backtesting;

/// <summary>
/// Analiza datos históricos de un symbol para calcular un <see cref="SymbolProfile"/>
/// que permite adaptar parámetros de RiskConfig e IndicatorConfig calibrados
/// para BTC/ETH a cualquier otro par de trading.
/// <para>
/// Reutiliza las klines ya descargadas por el Ranker — cero llamadas extra a Binance.
/// </para>
/// </summary>
internal static class SymbolProfiler
{
    private const int AtrPeriod = 14;
    private const int BbPeriod  = 20;
    private const decimal BbStdDev = 2.0m;

    /// <summary>
    /// Calcula el perfil de un symbol a partir de klines históricas.
    /// </summary>
    /// <param name="klines">Klines históricas (mínimo 30 para resultados significativos).</param>
    /// <param name="currentSpreadPercent">Spread actual bid-ask (%). 0 si no disponible.</param>
    public static SymbolProfile Analyze(
        IReadOnlyList<Kline> klines,
        decimal currentSpreadPercent = 0m)
    {
        if (klines.Count < BbPeriod + 1)
        {
            return new SymbolProfile(
                MedianAtrPercent: 0.03m,
                MedianBandWidth: 0.08m,
                CurrentSpreadPercent: currentSpreadPercent,
                VolumeCV: 0m,
                AdjustedHighVolatilityAtrPercent: 0.06m,
                AdjustedHighVolatilityBandWidthPercent: 0.16m,
                AdjustedMaxSpreadPercent: Math.Max(currentSpreadPercent * 3m, 0.1m),
                AdjustedVolumeMinRatio: 1.5m);
        }

        var atrPercents = CalculateAtrPercents(klines);
        var bandWidths  = CalculateBandWidths(klines);
        var volumeCV    = CalculateVolumeCV(klines);

        var medianAtrPct = Median(atrPercents);
        var medianBW     = Median(bandWidths);

        return new SymbolProfile(
            MedianAtrPercent: medianAtrPct,
            MedianBandWidth: medianBW,
            CurrentSpreadPercent: currentSpreadPercent,
            VolumeCV: volumeCV,
            AdjustedHighVolatilityAtrPercent: medianAtrPct * 2.0m,
            AdjustedHighVolatilityBandWidthPercent: medianBW * 2.0m,
            AdjustedMaxSpreadPercent: Math.Max(currentSpreadPercent * 3m, 0.1m),
            AdjustedVolumeMinRatio: volumeCV switch
            {
                < 0.5m  => 1.3m,
                < 1.0m  => 1.5m,
                _       => 2.0m
            });
    }

    private static List<decimal> CalculateAtrPercents(IReadOnlyList<Kline> klines)
    {
        var atrPercents = new List<decimal>();
        decimal prevClose = klines[0].Close;
        decimal atr = 0m;

        for (var i = 1; i < klines.Count; i++)
        {
            var k = klines[i];
            var tr = Math.Max(k.High - k.Low,
                     Math.Max(Math.Abs(k.High - prevClose),
                              Math.Abs(k.Low - prevClose)));

            atr = i <= AtrPeriod
                ? (atr * (i - 1) + tr) / i
                : (atr * (AtrPeriod - 1) + tr) / AtrPeriod;

            if (i >= AtrPeriod && k.Close > 0)
                atrPercents.Add(atr / k.Close);

            prevClose = k.Close;
        }

        return atrPercents;
    }

    private static List<decimal> CalculateBandWidths(IReadOnlyList<Kline> klines)
    {
        var bandWidths = new List<decimal>();
        var window = new Queue<decimal>(BbPeriod);

        foreach (var k in klines)
        {
            window.Enqueue(k.Close);
            if (window.Count > BbPeriod)
                window.Dequeue();

            if (window.Count < BbPeriod)
                continue;

            var values = window.ToArray();
            var mean = values.Average();
            if (mean <= 0) continue;

            var variance = values.Sum(v => (v - mean) * (v - mean)) / BbPeriod;
            var stdDev = (decimal)Math.Sqrt((double)variance);
            var upper = mean + BbStdDev * stdDev;
            var lower = mean - BbStdDev * stdDev;
            var bandWidth = (upper - lower) / mean;

            bandWidths.Add(bandWidth);
        }

        return bandWidths;
    }

    private static decimal CalculateVolumeCV(IReadOnlyList<Kline> klines)
    {
        var volumes = klines.Select(k => k.Volume).Where(v => v > 0).ToList();
        if (volumes.Count < 2)
            return 0m;

        var mean = volumes.Average();
        if (mean <= 0)
            return 0m;

        var variance = volumes.Sum(v => (v - mean) * (v - mean)) / volumes.Count;
        var stdDev = (decimal)Math.Sqrt((double)variance);

        return stdDev / mean;
    }

    private static decimal Median(List<decimal> values)
    {
        if (values.Count == 0)
            return 0m;

        values.Sort();
        var mid = values.Count / 2;
        return values.Count % 2 == 0
            ? (values[mid - 1] + values[mid]) / 2m
            : values[mid];
    }
}
