using FluentAssertions;
using TradingBot.Application.Backtesting;
using TradingBot.Core.Enums;

namespace TradingBot.Application.Tests.Backtesting;

public sealed class BacktestMetricsTests
{
    private static BacktestTrade MakeTrade(decimal netPnL, decimal grossPnL = 0m) => new(
        OrderSide.Buy, 50000m, 50000m + netPnL * 100, 0.01m,
        grossPnL == 0m ? netPnL : grossPnL,
        Math.Abs(netPnL) * 0.001m, // fees
        Math.Abs(netPnL) * 0.0005m, // slippage
        netPnL,
        DateTimeOffset.UtcNow.AddMinutes(-10),
        DateTimeOffset.UtcNow,
        "Test");

    [Fact]
    public void Calculate_WhenNoTrades_ReturnsAllZeros()
    {
        var metrics = BacktestMetrics.Calculate([]);

        metrics.SharpeRatio.Should().Be(0);
        metrics.SortinoRatio.Should().Be(0);
        metrics.CalmarRatio.Should().Be(0);
        metrics.ProfitFactor.Should().Be(0);
        metrics.MaxConsecutiveLosses.Should().Be(0);
        metrics.MaxConsecutiveWins.Should().Be(0);
        metrics.Expectancy.Should().Be(0);
    }

    [Fact]
    public void Calculate_WhenAllWins_SharpeIsPositive()
    {
        var trades = new List<BacktestTrade>
        {
            MakeTrade(10m), MakeTrade(15m), MakeTrade(12m), MakeTrade(8m), MakeTrade(20m)
        };

        var metrics = BacktestMetrics.Calculate(trades);

        metrics.SharpeRatio.Should().BeGreaterThan(0);
        metrics.MaxConsecutiveWins.Should().Be(5);
        metrics.MaxConsecutiveLosses.Should().Be(0);
    }

    [Fact]
    public void Calculate_WhenAllLosses_SharpeIsNegative()
    {
        var trades = new List<BacktestTrade>
        {
            MakeTrade(-5m), MakeTrade(-10m), MakeTrade(-3m), MakeTrade(-7m)
        };

        var metrics = BacktestMetrics.Calculate(trades);

        metrics.SharpeRatio.Should().BeLessThan(0);
        metrics.MaxConsecutiveLosses.Should().Be(4);
        metrics.MaxConsecutiveWins.Should().Be(0);
    }

    [Fact]
    public void Calculate_ProfitFactor_IsGrossWinsDividedByGrossLosses()
    {
        // 3 wins of 20 each (gross = 60), 2 losses of 10 each (gross = 20)
        // PF = 60/20 = 3.0
        var trades = new List<BacktestTrade>
        {
            MakeTrade(20m), MakeTrade(20m), MakeTrade(20m),
            MakeTrade(-10m), MakeTrade(-10m)
        };

        var metrics = BacktestMetrics.Calculate(trades);

        metrics.ProfitFactor.Should().Be(3m);
    }

    [Fact]
    public void Calculate_WhenNoLosses_ProfitFactorIsCapped()
    {
        var trades = new List<BacktestTrade> { MakeTrade(10m), MakeTrade(20m) };

        var metrics = BacktestMetrics.Calculate(trades);

        metrics.ProfitFactor.Should().Be(999m);
    }

    [Fact]
    public void Calculate_Expectancy_MatchesFormula()
    {
        // 4 trades: 3 wins (10, 20, 30) = 60 total; 1 loss (-15) = 15 total
        // WinRate = 3/4 = 0.75, LossRate = 0.25
        // AvgWin = 60/3 = 20, AvgLoss = 15/1 = 15
        // E = 0.75 * 20 - 0.25 * 15 = 15 - 3.75 = 11.25
        var trades = new List<BacktestTrade>
        {
            MakeTrade(10m), MakeTrade(20m), MakeTrade(30m), MakeTrade(-15m)
        };

        var metrics = BacktestMetrics.Calculate(trades);

        metrics.Expectancy.Should().Be(11.25m);
    }

    [Fact]
    public void Calculate_SortinoRatio_OnlyPenalizesDownside()
    {
        // High positive returns + one small negative → high Sortino
        var trades = new List<BacktestTrade>
        {
            MakeTrade(50m), MakeTrade(40m), MakeTrade(30m), MakeTrade(-2m)
        };

        var metrics = BacktestMetrics.Calculate(trades);

        metrics.SortinoRatio.Should().BeGreaterThan(metrics.SharpeRatio,
            "Sortino should be higher than Sharpe when most variance is upside");
    }

    [Fact]
    public void Calculate_CalmarRatio_IsTotalReturnOverMaxDrawdown()
    {
        // Total PnL = +10, drawdown exists
        var trades = new List<BacktestTrade>
        {
            MakeTrade(20m), MakeTrade(-15m), MakeTrade(5m)
        };

        var metrics = BacktestMetrics.Calculate(trades);

        metrics.CalmarRatio.Should().BeGreaterThan(0);
    }

    [Fact]
    public void Calculate_MaxConsecutiveStreaks_TracksCorrectly()
    {
        // W W L L L W W W L
        var trades = new List<BacktestTrade>
        {
            MakeTrade(10m), MakeTrade(5m),
            MakeTrade(-3m), MakeTrade(-7m), MakeTrade(-2m),
            MakeTrade(15m), MakeTrade(8m), MakeTrade(12m),
            MakeTrade(-1m)
        };

        var metrics = BacktestMetrics.Calculate(trades);

        metrics.MaxConsecutiveWins.Should().Be(3);
        metrics.MaxConsecutiveLosses.Should().Be(3);
    }

    [Fact]
    public void Calculate_WithKnownReturns_SharpeIsPositive()
    {
        // Returns: [10, -5, 10, -5, 10] → mean=4, positive overall
        var trades = new List<BacktestTrade>
        {
            MakeTrade(10m), MakeTrade(-5m), MakeTrade(10m),
            MakeTrade(-5m), MakeTrade(10m)
        };

        var metrics = BacktestMetrics.Calculate(trades);

        metrics.SharpeRatio.Should().BeGreaterThan(0,
            "positive mean return should produce positive Sharpe");
        metrics.SharpeRatio.Should().BeLessThan(1m,
            "with high variance relative to mean, Sharpe should be moderate");
    }

    [Fact]
    public void Calculate_NetPnLLessThanGross_WhenFeesApplied()
    {
        // Verify the concept: gross PnL > net PnL due to fees
        var trade = new BacktestTrade(
            OrderSide.Buy, 50000m, 51000m, 0.01m,
            GrossPnL: 10m,   // gross
            Fees: 1.5m,      // fees
            SlippageCost: 0.5m,
            NetPnL: 8m,      // net = gross - fees - slippage
            DateTimeOffset.UtcNow.AddMinutes(-5),
            DateTimeOffset.UtcNow,
            "Test");

        trade.NetPnL.Should().BeLessThan(trade.GrossPnL,
            "net P&L should always be less than gross P&L when fees exist");
        trade.Fees.Should().BeGreaterThan(0);
    }
}
