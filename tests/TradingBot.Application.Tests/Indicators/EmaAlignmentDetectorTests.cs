using FluentAssertions;
using TradingBot.Application.Strategies.Indicators;

namespace TradingBot.Application.Tests.Indicators;

public sealed class EmaAlignmentDetectorTests
{
    [Fact]
    public void IsBullishAligned_WhenPricesRising_ReturnsTrue()
    {
        var detector = new EmaAlignmentDetector();
        for (var i = 0; i < 60; i++)
            detector.Update(100m + i);

        detector.IsReady.Should().BeTrue();
        detector.IsBullishAligned.Should().BeTrue();
        detector.IsBearishAligned.Should().BeFalse();
    }

    [Fact]
    public void IsBearishAligned_WhenPricesFalling_ReturnsTrue()
    {
        var detector = new EmaAlignmentDetector();
        for (var i = 0; i < 60; i++)
            detector.Update(200m - i);

        detector.IsReady.Should().BeTrue();
        detector.IsBearishAligned.Should().BeTrue();
        detector.IsBullishAligned.Should().BeFalse();
    }

    [Fact]
    public void IsFlat_WhenPricesConstant_ReturnsTrue()
    {
        var detector = new EmaAlignmentDetector();
        for (var i = 0; i < 60; i++)
            detector.Update(100m);

        detector.IsReady.Should().BeTrue();
        detector.IsFlat().Should().BeTrue();
    }

    [Fact]
    public void Ema50Slope_WhenPricesRising_IsPositive()
    {
        var detector = new EmaAlignmentDetector();
        for (var i = 0; i < 60; i++)
            detector.Update(100m + i);

        detector.Ema50Slope.Should().NotBeNull();
        detector.Ema50Slope!.Value.Should().BePositive();
    }

    [Fact]
    public void IsReady_WhenInsufficientData_ReturnsFalse()
    {
        var detector = new EmaAlignmentDetector();
        for (var i = 0; i < 10; i++)
            detector.Update(100m + i);

        detector.IsReady.Should().BeFalse();
        detector.IsBullishAligned.Should().BeFalse();
    }

    [Fact]
    public void Reset_ClearsState()
    {
        var detector = new EmaAlignmentDetector();
        for (var i = 0; i < 60; i++)
            detector.Update(100m + i);

        detector.Reset();
        detector.IsReady.Should().BeFalse();
        detector.Ema50Slope.Should().BeNull();
    }
}
