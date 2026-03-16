using FluentAssertions;
using TradingBot.Application.Strategies.Indicators;
using TradingBot.Core.Enums;

namespace TradingBot.Application.Tests.Indicators;

public sealed class AdxIndicatorTests
{
    [Fact]
    public void Type_ReturnsADX()
    {
        var adx = new AdxIndicator(14);
        adx.Type.Should().Be(IndicatorType.ADX);
    }

    [Fact]
    public void Name_IncludesPeriod()
    {
        var adx = new AdxIndicator(14);
        adx.Name.Should().Be("ADX(14)");
    }

    [Fact]
    public void Constructor_WhenPeriodTooSmall_Throws()
    {
        var act = () => new AdxIndicator(1);
        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void IsReady_WhenNotEnoughData_ReturnsFalse()
    {
        var adx = new AdxIndicator(14);
        for (var i = 0; i < 10; i++)
            adx.Update(100m + i);

        adx.IsReady.Should().BeFalse();
        adx.Calculate().Should().BeNull();
    }

    [Fact]
    public void IsReady_WhenEnoughData_ReturnsTrue()
    {
        var adx = new AdxIndicator(5);
        // Need period * 2 data points
        for (var i = 0; i < 15; i++)
            adx.Update(100m + i);

        adx.IsReady.Should().BeTrue();
    }

    [Fact]
    public void Calculate_WithStrongUptrend_ReturnsHighAdx()
    {
        var adx = new AdxIndicator(5);
        // Strong consistent uptrend
        for (var i = 0; i < 30; i++)
            adx.Update(100m + i * 5m);

        var value = adx.Calculate();
        value.Should().NotBeNull();
        value!.Value.Should().BeGreaterThan(0m);
    }

    [Fact]
    public void PlusDi_InUptrend_IsGreaterThanMinusDi()
    {
        var adx = new AdxIndicator(5);
        for (var i = 0; i < 30; i++)
            adx.Update(100m + i * 3m);

        adx.PlusDi.Should().NotBeNull();
        adx.MinusDi.Should().NotBeNull();
        adx.IsBullish.Should().BeTrue();
        adx.IsBearish.Should().BeFalse();
    }

    [Fact]
    public void MinusDi_InDowntrend_IsGreaterThanPlusDi()
    {
        var adx = new AdxIndicator(5);
        for (var i = 0; i < 30; i++)
            adx.Update(200m - i * 3m);

        adx.MinusDi.Should().NotBeNull();
        adx.IsBearish.Should().BeTrue();
        adx.IsBullish.Should().BeFalse();
    }

    [Fact]
    public void Calculate_WithFlatPrices_ReturnsLowAdx()
    {
        var adx = new AdxIndicator(5);
        for (var i = 0; i < 30; i++)
            adx.Update(100m);

        var value = adx.Calculate();
        // Flat prices should not indicate a strong trend
        // ADX should be 0 or close to it
        value.Should().NotBeNull();
        value!.Value.Should().BeLessThan(10m);
    }

    [Fact]
    public void Reset_ClearsState()
    {
        var adx = new AdxIndicator(5);
        for (var i = 0; i < 30; i++)
            adx.Update(100m + i);

        adx.IsReady.Should().BeTrue();
        adx.Reset();
        adx.IsReady.Should().BeFalse();
        adx.Calculate().Should().BeNull();
        adx.PlusDi.Should().BeNull();
        adx.MinusDi.Should().BeNull();
    }
}
