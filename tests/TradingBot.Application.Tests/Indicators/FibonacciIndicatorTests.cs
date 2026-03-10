using FluentAssertions;
using TradingBot.Application.Strategies.Indicators;
using TradingBot.Core.Enums;

namespace TradingBot.Application.Tests.Indicators;

public sealed class FibonacciIndicatorTests
{
    // ── Properties ────────────────────────────────────────────────────────

    [Fact]
    public void Type_ReturnsFibonacci()
    {
        var sut = new FibonacciIndicator();

        sut.Type.Should().Be(IndicatorType.Fibonacci);
    }

    [Fact]
    public void Name_IncludesPeriod()
    {
        var sut = new FibonacciIndicator(30);

        sut.Name.Should().Be("Fib(30)");
    }

    // ── Constructor validation ────────────────────────────────────────────

    [Fact]
    public void Constructor_WhenPeriodTooSmall_Throws()
    {
        var act = () => new FibonacciIndicator(period: 1);

        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    // ── IsReady ───────────────────────────────────────────────────────────

    [Fact]
    public void IsReady_WhenNotEnoughData_ReturnsFalse()
    {
        var sut = new FibonacciIndicator(5);

        for (var i = 0; i < 4; i++)
            sut.Update(100m + i);

        sut.IsReady.Should().BeFalse();
        sut.Calculate().Should().BeNull();
    }

    [Fact]
    public void IsReady_WhenEnoughData_ReturnsTrue()
    {
        var sut = new FibonacciIndicator(5);

        for (var i = 0; i < 5; i++)
            sut.Update(100m + i);

        sut.IsReady.Should().BeTrue();
    }

    // ── Levels calculation ────────────────────────────────────────────────

    [Fact]
    public void Levels_WithKnownRange_ReturnsCorrectFibonacciLevels()
    {
        var sut = new FibonacciIndicator(5);

        // Low = 100, High = 200, Range = 100
        sut.Update(100m);
        sut.Update(150m);
        sut.Update(200m);
        sut.Update(120m);
        sut.Update(180m);

        var levels = sut.Levels;
        levels.Should().NotBeNull();

        // Level = High - Range * ratio
        levels![0.236m].Should().Be(200m - 100m * 0.236m); // 176.4
        levels[0.382m].Should().Be(200m - 100m * 0.382m);  // 161.8
        levels[0.500m].Should().Be(200m - 100m * 0.500m);  // 150.0
        levels[0.618m].Should().Be(200m - 100m * 0.618m);  // 138.2
        levels[0.786m].Should().Be(200m - 100m * 0.786m);  // 121.4
    }

    [Fact]
    public void Calculate_ReturnsGoldenRatioLevel()
    {
        var sut = new FibonacciIndicator(5);

        sut.Update(100m);
        sut.Update(150m);
        sut.Update(200m);
        sut.Update(120m);
        sut.Update(180m);

        // Calculate returns 0.618 level: 200 - 100 * 0.618 = 138.2
        sut.Calculate().Should().Be(200m - 100m * 0.618m);
    }

    [Fact]
    public void Levels_WhenFlatPrice_AllLevelsEqualPrice()
    {
        var sut = new FibonacciIndicator(3);

        sut.Update(50m);
        sut.Update(50m);
        sut.Update(50m);

        var levels = sut.Levels;
        levels.Should().NotBeNull();

        foreach (var (_, level) in levels!)
            level.Should().Be(50m);
    }

    // ── GetNearestLevel ───────────────────────────────────────────────────

    [Fact]
    public void GetNearestLevel_WhenPriceNearLevel_ReturnsRatio()
    {
        var sut = new FibonacciIndicator(5);

        // Low = 100, High = 200, Range = 100
        sut.Update(100m);
        sut.Update(150m);
        sut.Update(200m);
        sut.Update(120m);
        sut.Update(180m);

        // 0.618 level = 138.2, price = 138.5 (0.22% away, within 0.5% tolerance)
        var nearest = sut.GetNearestLevel(138.5m);
        nearest.Should().Be(0.618m);
    }

    [Fact]
    public void GetNearestLevel_WhenPriceFarFromAllLevels_ReturnsNull()
    {
        var sut = new FibonacciIndicator(5);

        sut.Update(100m);
        sut.Update(150m);
        sut.Update(200m);
        sut.Update(120m);
        sut.Update(180m);

        // Price 160 is between 0.382 (161.8) and 0.5 (150.0) levels
        // but not close enough to either with 0.5% tolerance
        var nearest = sut.GetNearestLevel(155m, tolerancePercent: 0.1m);
        nearest.Should().BeNull();
    }

    [Fact]
    public void GetNearestLevel_WhenNotReady_ReturnsNull()
    {
        var sut = new FibonacciIndicator(10);
        sut.Update(100m);

        sut.GetNearestLevel(100m).Should().BeNull();
    }

    // ── High / Low ────────────────────────────────────────────────────────

    [Fact]
    public void HighAndLow_TrackCorrectValues()
    {
        var sut = new FibonacciIndicator(4);

        sut.Update(110m);
        sut.Update(90m);
        sut.Update(130m);
        sut.Update(80m);

        sut.High.Should().Be(130m);
        sut.Low.Should().Be(80m);
    }

    // ── Reset ─────────────────────────────────────────────────────────────

    [Fact]
    public void Reset_ClearsAllState()
    {
        var sut = new FibonacciIndicator(3);

        sut.Update(100m);
        sut.Update(200m);
        sut.Update(150m);
        sut.IsReady.Should().BeTrue();

        sut.Reset();

        sut.IsReady.Should().BeFalse();
        sut.Calculate().Should().BeNull();
        sut.Levels.Should().BeNull();
    }

    // ── Buffer sliding window ─────────────────────────────────────────────

    [Fact]
    public void Update_SlidesWindowWhenBufferFull()
    {
        var sut = new FibonacciIndicator(3);

        sut.Update(100m); // [100]
        sut.Update(200m); // [100, 200]
        sut.Update(150m); // [100, 200, 150] → High=200, Low=100

        sut.High.Should().Be(200m);
        sut.Low.Should().Be(100m);

        sut.Update(120m); // [200, 150, 120] → 100 removed, High=200, Low=120

        sut.High.Should().Be(200m);
        sut.Low.Should().Be(120m);

        sut.Update(110m); // [150, 120, 110] → 200 removed, High=150, Low=110

        sut.High.Should().Be(150m);
        sut.Low.Should().Be(110m);
    }
}
