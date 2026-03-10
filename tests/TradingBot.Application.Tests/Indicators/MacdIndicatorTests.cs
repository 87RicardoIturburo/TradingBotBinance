using FluentAssertions;
using TradingBot.Application.Strategies.Indicators;
using TradingBot.Core.Enums;

namespace TradingBot.Application.Tests.Indicators;

public sealed class MacdIndicatorTests
{
    // ── Properties ────────────────────────────────────────────────────────

    [Fact]
    public void Type_ReturnsMACD()
    {
        var sut = new MacdIndicator();

        sut.Type.Should().Be(IndicatorType.MACD);
    }

    [Fact]
    public void Name_IncludesAllPeriods()
    {
        var sut = new MacdIndicator(12, 26, 9);

        sut.Name.Should().Be("MACD(12,26,9)");
    }

    // ── Constructor validation ────────────────────────────────────────────

    [Fact]
    public void Constructor_WhenFastPeriodTooSmall_Throws()
    {
        var act = () => new MacdIndicator(fastPeriod: 1, slowPeriod: 26, signalPeriod: 9);

        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void Constructor_WhenSlowPeriodNotGreaterThanFast_Throws()
    {
        var act = () => new MacdIndicator(fastPeriod: 12, slowPeriod: 12, signalPeriod: 9);

        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void Constructor_WhenSignalPeriodTooSmall_Throws()
    {
        var act = () => new MacdIndicator(fastPeriod: 12, slowPeriod: 26, signalPeriod: 1);

        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    // ── IsReady ───────────────────────────────────────────────────────────

    [Fact]
    public void IsReady_WhenNotEnoughData_ReturnsFalse()
    {
        var sut = new MacdIndicator(3, 5, 3);

        for (var i = 0; i < 4; i++)
            sut.Update(100m + i);

        sut.IsReady.Should().BeFalse();
    }

    [Fact]
    public void IsReady_WhenEnoughData_ReturnsTrue()
    {
        var sut = new MacdIndicator(3, 5, 3);

        // Need: slowPeriod (5) to warm up EMAs + signalPeriod (3) for signal line
        for (var i = 0; i < 8; i++)
            sut.Update(100m + i);

        sut.IsReady.Should().BeTrue();
    }

    // ── Calculate ─────────────────────────────────────────────────────────

    [Fact]
    public void Calculate_WhenNotReady_ReturnsNull()
    {
        var sut = new MacdIndicator(3, 5, 3);
        sut.Update(100m);

        sut.Calculate().Should().BeNull();
    }

    [Fact]
    public void Calculate_WithAscendingPrices_ReturnsPositiveMacd()
    {
        var sut = new MacdIndicator(3, 5, 3);

        // Ascending prices → fast EMA > slow EMA → positive MACD
        for (var i = 0; i < 10; i++)
            sut.Update(100m + i * 5m);

        sut.IsReady.Should().BeTrue();
        sut.Calculate().Should().BeGreaterThan(0m);
    }

    [Fact]
    public void Calculate_WithDescendingPrices_ReturnsNegativeMacd()
    {
        var sut = new MacdIndicator(3, 5, 3);

        // Descending prices → fast EMA < slow EMA → negative MACD
        for (var i = 0; i < 10; i++)
            sut.Update(200m - i * 5m);

        sut.IsReady.Should().BeTrue();
        sut.Calculate().Should().BeLessThan(0m);
    }

    [Fact]
    public void Calculate_WithFlatPrices_ReturnsNearZero()
    {
        var sut = new MacdIndicator(3, 5, 3);

        for (var i = 0; i < 10; i++)
            sut.Update(100m);

        sut.IsReady.Should().BeTrue();
        sut.Calculate().Should().BeApproximately(0m, 0.0001m);
    }

    // ── SignalLine ────────────────────────────────────────────────────────

    [Fact]
    public void SignalLine_WhenNotReady_ReturnsNull()
    {
        var sut = new MacdIndicator(3, 5, 3);
        sut.Update(100m);

        sut.SignalLine.Should().BeNull();
    }

    [Fact]
    public void SignalLine_WhenReady_ReturnsNonNull()
    {
        var sut = new MacdIndicator(3, 5, 3);

        for (var i = 0; i < 10; i++)
            sut.Update(100m + i);

        sut.SignalLine.Should().NotBeNull();
    }

    // ── Histogram ─────────────────────────────────────────────────────────

    [Fact]
    public void Histogram_WhenNotReady_ReturnsNull()
    {
        var sut = new MacdIndicator(3, 5, 3);
        sut.Update(100m);

        sut.Histogram.Should().BeNull();
    }

    [Fact]
    public void Histogram_WhenReady_EqualsMacdMinusSignal()
    {
        var sut = new MacdIndicator(3, 5, 3);

        for (var i = 0; i < 10; i++)
            sut.Update(100m + i * 2m);

        var macd      = sut.Calculate()!.Value;
        var signal    = sut.SignalLine!.Value;
        var histogram = sut.Histogram!.Value;

        histogram.Should().BeApproximately(macd - signal, 0.0001m);
    }

    // ── Reset ─────────────────────────────────────────────────────────────

    [Fact]
    public void Reset_ClearsState()
    {
        var sut = new MacdIndicator(3, 5, 3);

        for (var i = 0; i < 10; i++)
            sut.Update(100m + i);

        sut.IsReady.Should().BeTrue();

        sut.Reset();

        sut.IsReady.Should().BeFalse();
        sut.Calculate().Should().BeNull();
        sut.SignalLine.Should().BeNull();
        sut.Histogram.Should().BeNull();
    }
}
