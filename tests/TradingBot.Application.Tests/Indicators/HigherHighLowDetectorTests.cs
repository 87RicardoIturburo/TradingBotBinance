using FluentAssertions;
using TradingBot.Application.Strategies.Indicators;

namespace TradingBot.Application.Tests.Indicators;

public sealed class HigherHighLowDetectorTests
{
    [Fact]
    public void HasHigherHighs_ThreeConsecutiveRisingHighs_ReturnsTrue()
    {
        var detector = new HigherHighLowDetector();
        detector.Update(100m, 90m);
        detector.Update(105m, 95m);
        detector.Update(110m, 100m);

        detector.IsReady().Should().BeTrue();
        detector.HasHigherHighs().Should().BeTrue();
    }

    [Fact]
    public void HasHigherHighs_MixedPattern_ReturnsFalse()
    {
        var detector = new HigherHighLowDetector();
        detector.Update(100m, 90m);
        detector.Update(95m, 85m);
        detector.Update(110m, 100m);

        detector.HasHigherHighs().Should().BeFalse();
    }

    [Fact]
    public void HasLowerLows_ThreeConsecutiveFallingLows_ReturnsTrue()
    {
        var detector = new HigherHighLowDetector();
        detector.Update(100m, 90m);
        detector.Update(95m, 85m);
        detector.Update(90m, 80m);

        detector.HasLowerLows().Should().BeTrue();
    }

    [Fact]
    public void Update_ExceedsBufferSize_OldestDropped()
    {
        var detector = new HigherHighLowDetector(3);
        detector.Update(100m, 90m);
        detector.Update(105m, 95m);
        detector.Update(110m, 100m);
        detector.Update(80m, 70m);

        detector.HasHigherHighs().Should().BeFalse();
    }

    [Fact]
    public void IsReady_InsufficientData_ReturnsFalse()
    {
        var detector = new HigherHighLowDetector();
        detector.Update(100m, 90m);

        detector.IsReady().Should().BeFalse();
        detector.HasHigherHighs().Should().BeFalse();
    }

    [Fact]
    public void Reset_ClearsBuffer()
    {
        var detector = new HigherHighLowDetector();
        detector.Update(100m, 90m);
        detector.Update(105m, 95m);
        detector.Update(110m, 100m);

        detector.Reset();
        detector.IsReady().Should().BeFalse();
    }
}
