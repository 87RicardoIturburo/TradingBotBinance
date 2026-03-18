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
    /// <summary>
    /// Tope para ratios cuando el denominador es 0 o insignificante.
    /// Valores como 999 distorsionan comparaciones y el UI.
    /// </summary>
    private const decimal RatioCap = 10m;

    /// <summary>
    /// Mínimo de trades Y días de backtest para anualizar Sharpe/Sortino.
    /// Con menos datos, la anualización amplifica ruido estadístico.
    /// </summary>
    private const int MinTradesForAnnualization = 20;
    private const double MinDaysForAnnualization = 30;

    /// <summary>Calcula las métricas a partir de los trades completados.</summary>
    public static BacktestMetrics Calculate(IReadOnlyList<BacktestTrade> trades)
    {
        if (trades.Count == 0)
            return new BacktestMetrics(0, 0, 0, 0, 0, 0, 0);

        var returns = trades.Select(t => t.NetPnL).ToList();

        // TRADE-4 fix: usar retornos porcentuales para Sharpe/Sortino.
        // ReturnPct = NetPnL / (EntryPrice × Quantity) para que sea comparable entre activos.
        var pctReturns = trades
            .Select(t =>
            {
                var invested = t.EntryPrice * t.Quantity;
                return invested > 0 ? t.NetPnL / invested : 0m;
            })
            .ToList();

        var meanPctReturn = pctReturns.Average();

        // Sharpe Ratio: (meanReturn / stdDev)
        // Solo anualizar con suficientes datos (≥20 trades Y ≥30 días de backtest).
        // Con pocos datos la anualización amplifica ruido: 4 trades en 7 días
        // produce annualizationFactor ≈ 12, convirtiendo un Sharpe crudo de 0.9 en 11.
        var stdDev = StandardDeviation(pctReturns);
        var rawSharpe = stdDev > 0 ? meanPctReturn / stdDev : 0m;

        decimal annualizationFactor = 1m;
        double totalDays = 0;
        if (trades.Count >= 2)
        {
            totalDays = (trades[^1].ExitTime - trades[0].EntryTime).TotalDays;
            if (totalDays >= MinDaysForAnnualization && trades.Count >= MinTradesForAnnualization)
            {
                var tradesPerYear = (decimal)(trades.Count / totalDays * 252); // 252 trading days
                annualizationFactor = (decimal)Math.Sqrt((double)Math.Max(tradesPerYear, 1m));
            }
        }
        var sharpe = Clamp(rawSharpe * annualizationFactor, -RatioCap, RatioCap);

        // Sortino Ratio (solo desviación negativa).
        // Con 0 o 1 retornos negativos, StandardDeviation retorna 0 →
        // no podemos calcular un Sortino significativo. Usamos RatioCap como tope.
        var negativePctReturns = pctReturns.Where(r => r < 0).ToList();
        var downsideDev = negativePctReturns.Count > 1 ? StandardDeviation(negativePctReturns) : 0m;
        decimal rawSortino;
        if (downsideDev > 0)
            rawSortino = meanPctReturn / downsideDev;
        else
            rawSortino = meanPctReturn > 0 ? RatioCap : 0m;
        var sortino = Clamp(rawSortino * annualizationFactor, -RatioCap, RatioCap);

        // Profit Factor (sobre P&L absoluto)
        var grossWins = returns.Where(r => r > 0).Sum();
        var grossLosses = Math.Abs(returns.Where(r => r < 0).Sum());
        var profitFactor = grossLosses > 0
            ? Math.Min(grossWins / grossLosses, RatioCap)
            : (grossWins > 0 ? RatioCap : 0m);

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
        var calmar = maxDrawdown > 0
            ? Clamp(totalReturn / maxDrawdown, -RatioCap, RatioCap)
            : (totalReturn > 0 ? RatioCap : 0m);

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
            SharpeRatio: Math.Round(sharpe, 2),
            SortinoRatio: Math.Round(sortino, 2),
            CalmarRatio: Math.Round(calmar, 2),
            ProfitFactor: Math.Round(profitFactor, 2),
            MaxConsecutiveLosses: maxConsecLosses,
            MaxConsecutiveWins: maxConsecWins,
            Expectancy: Math.Round(expectancy, 4));
    }

    private static decimal Clamp(decimal value, decimal min, decimal max) =>
        value < min ? min : value > max ? max : value;

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
