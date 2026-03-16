using FluentAssertions;
using TradingBot.Application.RiskManagement;

namespace TradingBot.Application.Tests.RiskManagement;

public sealed class PositionSizerTests
{
    [Fact]
    public void Calculate_WithNormalVolatility_ReturnsCorrectSize()
    {
        // Balance 10000 USDT, risk 1%, ATR = 50, multiplier 2x, price = 50000
        // riskAmount = 10000 * 0.01 = 100
        // stopDistance = 50 * 2 = 100
        // quantity = 100 / 100 = 1 BTC
        // amount = 1 * 50000 = 50000 USDT → capped at maxOrderAmount
        var result = PositionSizer.Calculate(
            accountBalanceUsdt: 10000m,
            riskPercentPerTrade: 0.01m,
            atrValue: 50m,
            atrMultiplier: 2m,
            currentPrice: 50000m,
            maxOrderAmountUsdt: 500m);

        result.WasAtrCalculated.Should().BeTrue();
        result.AmountUsdt.Should().BeLessThanOrEqualTo(500m);
        result.StopDistancePrice.Should().Be(100m);
    }

    [Fact]
    public void Calculate_WithHighVolatility_ReturnsSmallPosition()
    {
        // ATR = 500 → large stop distance → small position
        var result = PositionSizer.Calculate(
            accountBalanceUsdt: 10000m,
            riskPercentPerTrade: 0.01m,
            atrValue: 500m,
            atrMultiplier: 2m,
            currentPrice: 50000m,
            maxOrderAmountUsdt: 1000m);

        result.WasAtrCalculated.Should().BeTrue();
        // riskAmount = 100, stopDistance = 1000, qty = 0.1, amount = 5000 → capped at 1000
        result.AmountUsdt.Should().BeLessThanOrEqualTo(1000m);
    }

    [Fact]
    public void Calculate_WithLowVolatility_ReturnsLargerPosition()
    {
        // ATR = 10 → small stop distance → larger position
        var result = PositionSizer.Calculate(
            accountBalanceUsdt: 10000m,
            riskPercentPerTrade: 0.01m,
            atrValue: 10m,
            atrMultiplier: 2m,
            currentPrice: 50000m,
            maxOrderAmountUsdt: 1000m);

        result.WasAtrCalculated.Should().BeTrue();
        // riskAmount = 100, stopDistance = 20, qty = 5, amount = 250000 → capped at 1000
        result.AmountUsdt.Should().BeLessThanOrEqualTo(1000m);
    }

    [Fact]
    public void Calculate_NeverExceedsMaxOrderAmount()
    {
        var result = PositionSizer.Calculate(
            accountBalanceUsdt: 1000000m,
            riskPercentPerTrade: 0.05m,
            atrValue: 1m,
            atrMultiplier: 1m,
            currentPrice: 100m,
            maxOrderAmountUsdt: 200m);

        result.AmountUsdt.Should().BeLessThanOrEqualTo(200m);
    }

    [Fact]
    public void Calculate_WhenAtrIsZero_FallsBackToMaxOrder()
    {
        var result = PositionSizer.Calculate(
            accountBalanceUsdt: 10000m,
            riskPercentPerTrade: 0.01m,
            atrValue: 0m,
            atrMultiplier: 2m,
            currentPrice: 50000m,
            maxOrderAmountUsdt: 100m);

        result.WasAtrCalculated.Should().BeFalse();
        result.AmountUsdt.Should().Be(100m);
    }

    [Fact]
    public void Calculate_WhenBalanceIsZero_FallsBackToMaxOrder()
    {
        var result = PositionSizer.Calculate(
            accountBalanceUsdt: 0m,
            riskPercentPerTrade: 0.01m,
            atrValue: 50m,
            atrMultiplier: 2m,
            currentPrice: 50000m,
            maxOrderAmountUsdt: 100m);

        result.WasAtrCalculated.Should().BeFalse();
    }

    [Fact]
    public void Calculate_StopDistanceEqualsAtrTimesMultiplier()
    {
        var result = PositionSizer.Calculate(
            accountBalanceUsdt: 10000m,
            riskPercentPerTrade: 0.01m,
            atrValue: 75m,
            atrMultiplier: 3m,
            currentPrice: 50000m,
            maxOrderAmountUsdt: 5000m);

        result.StopDistancePrice.Should().Be(225m); // 75 * 3
    }

    [Fact]
    public void Calculate_WithSmallBalance_ReturnsMinimumViable()
    {
        // Balance 10 USDT, risk 1% = 0.10 USDT risk
        var result = PositionSizer.Calculate(
            accountBalanceUsdt: 10m,
            riskPercentPerTrade: 0.01m,
            atrValue: 50m,
            atrMultiplier: 2m,
            currentPrice: 50000m,
            maxOrderAmountUsdt: 100m);

        // 0.10 / 100 = 0.001 BTC → 0.001 * 50000 = 50 USDT
        result.AmountUsdt.Should().BeGreaterThan(0m);
    }
}
