using FluentAssertions;
using TradingBot.Application.Strategies.Indicators;
using TradingBot.Core.Enums;

namespace TradingBot.Application.Tests.Indicators;

public sealed class AtrIndicatorTests
{
    [Fact]
    public void Type_ReturnsATR()
    {
        var atr = new AtrIndicator(14);
        atr.Type.Should().Be(IndicatorType.ATR);
    }

    [Fact]
    public void Name_IncludesPeriod()
    {
        var atr = new AtrIndicator(14);
        atr.Name.Should().Be("ATR(14)");
    }

    [Fact]
    public void Constructor_WhenPeriodTooSmall_Throws()
    {
        var act = () => new AtrIndicator(1);
        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void IsReady_WhenNotEnoughData_ReturnsFalse()
    {
        var atr = new AtrIndicator(14);
        for (var i = 0; i < 10; i++)
            atr.Update(100m + i);

        atr.IsReady.Should().BeFalse();
    }

    [Fact]
    public void IsReady_WhenEnoughData_ReturnsTrue()
    {
        var atr = new AtrIndicator(5);
        for (var i = 0; i < 6; i++)
            atr.Update(100m + i);

        atr.IsReady.Should().BeTrue();
    }

    [Fact]
    public void Calculate_WithFlatPrices_ReturnsNearZero()
    {
        var atr = new AtrIndicator(5);
        for (var i = 0; i < 20; i++)
            atr.Update(100m);

        atr.Calculate().Should().NotBeNull();
        atr.Calculate()!.Value.Should().BeApproximately(0m, 0.001m);
    }

    [Fact]
    public void Calculate_WithVolatilePrices_ReturnsPositive()
    {
        var atr = new AtrIndicator(5);
        for (var i = 0; i < 20; i++)
            atr.Update(i % 2 == 0 ? 100m : 110m);

        atr.Calculate().Should().NotBeNull();
        atr.Calculate()!.Value.Should().BeGreaterThan(0m);
    }

    [Fact]
    public void Calculate_WithIncreasingVolatility_AtrIncreases()
    {
        var atr = new AtrIndicator(5);
        // Small moves first
        for (var i = 0; i < 10; i++)
            atr.Update(100m + (i % 2 == 0 ? 1m : -1m));

        var atrLow = atr.Calculate()!.Value;

        // Then big moves
        for (var i = 0; i < 20; i++)
            atr.Update(100m + (i % 2 == 0 ? 20m : -20m));

        var atrHigh = atr.Calculate()!.Value;
        atrHigh.Should().BeGreaterThan(atrLow);
    }

    [Fact]
    public void Reset_ClearsState()
    {
        var atr = new AtrIndicator(5);
        for (var i = 0; i < 20; i++)
            atr.Update(100m + i);

        atr.IsReady.Should().BeTrue();
        atr.Reset();
        atr.IsReady.Should().BeFalse();
        atr.Calculate().Should().BeNull();
    }

    [Fact]
    public void Value_WhenReady_EqualsCalculate()
    {
        var atr = new AtrIndicator(5);
        for (var i = 0; i < 10; i++)
            atr.Update(100m + i * 2);

        atr.Value.Should().Be(atr.Calculate());
    }
}
