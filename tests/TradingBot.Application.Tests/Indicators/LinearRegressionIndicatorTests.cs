using FluentAssertions;
using TradingBot.Application.Strategies.Indicators;
using TradingBot.Core.Enums;

namespace TradingBot.Application.Tests.Indicators;

public sealed class LinearRegressionIndicatorTests
{
    // ── Properties ────────────────────────────────────────────────────────

    [Fact]
    public void Type_ReturnsLinearRegression()
    {
        var sut = new LinearRegressionIndicator();

        sut.Type.Should().Be(IndicatorType.LinearRegression);
    }

    [Fact]
    public void Name_IncludesPeriod()
    {
        var sut = new LinearRegressionIndicator(15);

        sut.Name.Should().Be("LinReg(15)");
    }

    // ── Constructor validation ────────────────────────────────────────────

    [Fact]
    public void Constructor_WhenPeriodTooSmall_Throws()
    {
        var act = () => new LinearRegressionIndicator(period: 1);

        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    // ── IsReady ───────────────────────────────────────────────────────────

    [Fact]
    public void IsReady_WhenNotEnoughData_ReturnsFalse()
    {
        var sut = new LinearRegressionIndicator(5);

        for (var i = 0; i < 4; i++)
            sut.Update(100m + i);

        sut.IsReady.Should().BeFalse();
        sut.Calculate().Should().BeNull();
        sut.Slope.Should().BeNull();
        sut.RSquared.Should().BeNull();
    }

    [Fact]
    public void IsReady_WhenEnoughData_ReturnsTrue()
    {
        var sut = new LinearRegressionIndicator(5);

        for (var i = 0; i < 5; i++)
            sut.Update(100m + i);

        sut.IsReady.Should().BeTrue();
    }

    // ── Perfect uptrend ───────────────────────────────────────────────────

    [Fact]
    public void Slope_WithPerfectUptrend_IsPositive()
    {
        var sut = new LinearRegressionIndicator(5);

        // y = 100, 102, 104, 106, 108 (perfect linear uptrend, slope = 2)
        for (var i = 0; i < 5; i++)
            sut.Update(100m + i * 2m);

        sut.Slope.Should().BeGreaterThan(0m);
        sut.Slope.Should().BeApproximately(2m, 0.001m);
    }

    [Fact]
    public void RSquared_WithPerfectUptrend_IsOne()
    {
        var sut = new LinearRegressionIndicator(5);

        for (var i = 0; i < 5; i++)
            sut.Update(100m + i * 2m);

        sut.RSquared.Should().BeApproximately(1m, 0.0001m);
    }

    [Fact]
    public void Calculate_WithPerfectUptrend_ReturnsLastProjectedValue()
    {
        var sut = new LinearRegressionIndicator(5);

        // y = 100, 102, 104, 106, 108
        for (var i = 0; i < 5; i++)
            sut.Update(100m + i * 2m);

        // Projected value at x=4 = 100 + 2*4 = 108
        sut.Calculate().Should().BeApproximately(108m, 0.001m);
    }

    // ── Perfect downtrend ─────────────────────────────────────────────────

    [Fact]
    public void Slope_WithPerfectDowntrend_IsNegative()
    {
        var sut = new LinearRegressionIndicator(5);

        // y = 200, 198, 196, 194, 192
        for (var i = 0; i < 5; i++)
            sut.Update(200m - i * 2m);

        sut.Slope.Should().BeLessThan(0m);
        sut.Slope.Should().BeApproximately(-2m, 0.001m);
    }

    // ── Flat data ─────────────────────────────────────────────────────────

    [Fact]
    public void Slope_WithFlatData_IsZero()
    {
        var sut = new LinearRegressionIndicator(5);

        for (var i = 0; i < 5; i++)
            sut.Update(50m);

        sut.Slope.Should().Be(0m);
    }

    [Fact]
    public void RSquared_WithFlatData_IsOne()
    {
        var sut = new LinearRegressionIndicator(5);

        for (var i = 0; i < 5; i++)
            sut.Update(50m);

        // All predictions equal mean → R² = 1 (no variance to explain)
        sut.RSquared.Should().Be(1m);
    }

    // ── Noisy data ────────────────────────────────────────────────────────

    [Fact]
    public void RSquared_WithNoisyData_IsLow()
    {
        var sut = new LinearRegressionIndicator(6);

        // Zigzag: no clear trend
        sut.Update(100m);
        sut.Update(110m);
        sut.Update(95m);
        sut.Update(115m);
        sut.Update(90m);
        sut.Update(105m);

        sut.RSquared.Should().BeLessThan(0.5m);
    }

    // ── Reset ─────────────────────────────────────────────────────────────

    [Fact]
    public void Reset_ClearsAllState()
    {
        var sut = new LinearRegressionIndicator(3);

        sut.Update(100m);
        sut.Update(200m);
        sut.Update(300m);
        sut.IsReady.Should().BeTrue();

        sut.Reset();

        sut.IsReady.Should().BeFalse();
        sut.Calculate().Should().BeNull();
        sut.Slope.Should().BeNull();
        sut.RSquared.Should().BeNull();
    }

    // ── Sliding window ────────────────────────────────────────────────────

    [Fact]
    public void Update_SlidesWindowCorrectly()
    {
        var sut = new LinearRegressionIndicator(3);

        // Initial: uptrend 10, 20, 30
        sut.Update(10m);
        sut.Update(20m);
        sut.Update(30m);
        sut.Slope.Should().BeGreaterThan(0m);

        // Slide in downtrend: 20, 30, 10 → still slightly positive overall
        sut.Update(10m);

        // Now window = [20, 30, 10] → mixed, slope should change
        // This just verifies it doesn't crash and returns a valid number
        sut.Slope.Should().NotBeNull();
        sut.Calculate().Should().NotBeNull();
    }
}
