namespace TradingBot.Application.Backtesting;

/// <summary>
/// Métricas avanzadas de calidad para backtesting y optimización.
/// Permiten evaluar la robustez real de una estrategia más allá del P&amp;L bruto.
/// </summary>
public sealed record BacktestMetrics(
    /// <summary>Sharpe Ratio = meanReturn / stdDevReturn. Mayor = mejor ajustado al riesgo.</summary>
    decimal SharpeRatio,

    /// <summary>Sortino Ratio = meanReturn / downsideDeviation. Penaliza solo la volatilidad negativa.</summary>
    decimal SortinoRatio,

    /// <summary>Calmar Ratio = totalReturn / maxDrawdown. Rentabilidad vs peor caída.</summary>
    decimal CalmarRatio,

    /// <summary>Profit Factor = grossWins / grossLosses. > 1.5 es bueno.</summary>
    decimal ProfitFactor,

    /// <summary>Máxima racha de pérdidas consecutivas.</summary>
    int MaxConsecutiveLosses,

    /// <summary>Máxima racha de ganancias consecutivas.</summary>
    int MaxConsecutiveWins,

    /// <summary>Expectancy = (WinRate × AvgWin) − (LossRate × AvgLoss).</summary>
    decimal Expectancy)
{
    /// <summary>Calcula las métricas a partir de los trades completados.</summary>
    public static BacktestMetrics Calculate(IReadOnlyList<BacktestTrade> trades)
    {
        if (trades.Count == 0)
            return new BacktestMetrics(0, 0, 0, 0, 0, 0, 0);

        var returns = trades.Select(t => t.NetPnL).ToList();
        var meanReturn = returns.Average();

        // Sharpe Ratio
        var stdDev = StandardDeviation(returns);
        var sharpe = stdDev > 0 ? meanReturn / stdDev : 0m;

        // Sortino Ratio (solo desviación negativa)
        var negativeReturns = returns.Where(r => r < 0).ToList();
        var downsideDev = negativeReturns.Count > 0 ? StandardDeviation(negativeReturns) : 0m;
        var sortino = downsideDev > 0 ? meanReturn / downsideDev : (meanReturn > 0 ? 999m : 0m);

        // Profit Factor
        var grossWins = returns.Where(r => r > 0).Sum();
        var grossLosses = Math.Abs(returns.Where(r => r < 0).Sum());
        var profitFactor = grossLosses > 0 ? grossWins / grossLosses : (grossWins > 0 ? 999m : 0m);

        // Calmar Ratio: totalReturn / maxDrawdown
        decimal peakEquity = 0, maxDrawdown = 0, runningPnL = 0;
        foreach (var r in returns)
        {
            runningPnL += r;
            if (runningPnL > peakEquity) peakEquity = runningPnL;
            var dd = peakEquity - runningPnL;
            if (dd > maxDrawdown) maxDrawdown = dd;
        }
        var totalReturn = returns.Sum();
        var calmar = maxDrawdown > 0 ? totalReturn / maxDrawdown : (totalReturn > 0 ? 999m : 0m);

        // Consecutive wins/losses
        var (maxConsecWins, maxConsecLosses) = CalculateStreaks(returns);

        // Expectancy
        var wins = returns.Count(r => r > 0);
        var losses = returns.Count(r => r < 0);
        var winRate = trades.Count > 0 ? (decimal)wins / trades.Count : 0m;
        var lossRate = 1m - winRate;
        var avgWin = wins > 0 ? grossWins / wins : 0m;
        var avgLoss = losses > 0 ? grossLosses / losses : 0m;
        var expectancy = (winRate * avgWin) - (lossRate * avgLoss);

        return new BacktestMetrics(
            SharpeRatio: Math.Round(sharpe, 4),
            SortinoRatio: Math.Round(sortino, 4),
            CalmarRatio: Math.Round(calmar, 4),
            ProfitFactor: Math.Round(profitFactor, 4),
            MaxConsecutiveLosses: maxConsecLosses,
            MaxConsecutiveWins: maxConsecWins,
            Expectancy: Math.Round(expectancy, 4));
    }

    private static decimal StandardDeviation(IReadOnlyList<decimal> values)
    {
        if (values.Count <= 1) return 0m;
        var mean = values.Average();
        var sumSqDiff = values.Sum(v => (v - mean) * (v - mean));
        return (decimal)Math.Sqrt((double)(sumSqDiff / (values.Count - 1)));
    }

    private static (int MaxWins, int MaxLosses) CalculateStreaks(IReadOnlyList<decimal> returns)
    {
        int maxWins = 0, maxLosses = 0, currentWins = 0, currentLosses = 0;
        foreach (var r in returns)
        {
            if (r > 0)
            {
                currentWins++;
                currentLosses = 0;
                if (currentWins > maxWins) maxWins = currentWins;
            }
            else if (r < 0)
            {
                currentLosses++;
                currentWins = 0;
                if (currentLosses > maxLosses) maxLosses = currentLosses;
            }
            else
            {
                currentWins = 0;
                currentLosses = 0;
            }
        }
        return (maxWins, maxLosses);
    }
}
