using FluentAssertions;
using TradingBot.Application.Strategies.Indicators;
using TradingBot.Core.Enums;

namespace TradingBot.Application.Tests.Indicators;

public sealed class VolumeSmaIndicatorTests
{
    [Fact]
    public void Type_ReturnsVolume()
    {
        var sut = new VolumeSmaIndicator();

        sut.Type.Should().Be(IndicatorType.Volume);
    }

    [Fact]
    public void Name_IncludesPeriod()
    {
        var sut = new VolumeSmaIndicator(20);

        sut.Name.Should().Be("VolSMA(20)");
    }

    [Fact]
    public void Constructor_WhenPeriodTooSmall_Throws()
    {
        var act = () => new VolumeSmaIndicator(period: 1);

        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void IsReady_WhenNotEnoughData_ReturnsFalse()
    {
        var sut = new VolumeSmaIndicator(5);

        for (var i = 0; i < 4; i++)
            sut.UpdateVolume(1000m);

        sut.IsReady.Should().BeFalse();
    }

    [Fact]
    public void IsReady_WhenEnoughData_ReturnsTrue()
    {
        var sut = new VolumeSmaIndicator(5);

        for (var i = 0; i < 5; i++)
            sut.UpdateVolume(1000m);

        sut.IsReady.Should().BeTrue();
    }

    [Fact]
    public void Calculate_WhenNotReady_ReturnsNull()
    {
        var sut = new VolumeSmaIndicator(5);
        sut.UpdateVolume(1000m);

        sut.Calculate().Should().BeNull();
    }

    [Fact]
    public void Calculate_ReturnsAverageVolume()
    {
        var sut = new VolumeSmaIndicator(5);
        var volumes = new[] { 100m, 200m, 300m, 400m, 500m };

        foreach (var vol in volumes)
            sut.UpdateVolume(vol);

        sut.Calculate().Should().Be(300m);
    }

    [Fact]
    public void VolumeRatio_WhenHighVolume_ReturnsAboveOne()
    {
        var sut = new VolumeSmaIndicator(5);

        for (var i = 0; i < 5; i++)
            sut.UpdateVolume(100m);

        sut.UpdateVolume(200m);

        sut.VolumeRatio.Should().BeGreaterThan(1m);
    }

    [Fact]
    public void VolumeRatio_WhenLowVolume_ReturnsBelowOne()
    {
        var sut = new VolumeSmaIndicator(5);

        for (var i = 0; i < 5; i++)
            sut.UpdateVolume(1000m);

        sut.UpdateVolume(100m);

        sut.VolumeRatio.Should().BeLessThan(1m);
    }

    [Fact]
    public void VolumeRatio_WhenVeryHighVolume_ReturnsAbove1Point5()
    {
        var sut = new VolumeSmaIndicator(5);

        for (var i = 0; i < 5; i++)
            sut.UpdateVolume(100m);

        sut.UpdateVolume(200m);

        sut.VolumeRatio.Should().BeGreaterThanOrEqualTo(1.5m);
    }

    [Fact]
    public void LastVolume_TracksLastFedValue()
    {
        var sut = new VolumeSmaIndicator(5);
        sut.UpdateVolume(500m);
        sut.UpdateVolume(1000m);

        sut.LastVolume.Should().Be(1000m);
    }

    [Fact]
    public void Update_DelegatesToUpdateVolume()
    {
        var sut = new VolumeSmaIndicator(3);

        sut.Update(100m);
        sut.Update(200m);
        sut.Update(300m);

        sut.IsReady.Should().BeTrue();
        sut.Calculate().Should().Be(200m);
    }

    [Fact]
    public void Reset_ClearsState()
    {
        var sut = new VolumeSmaIndicator(5);

        for (var i = 0; i < 5; i++)
            sut.UpdateVolume(1000m);

        sut.IsReady.Should().BeTrue();

        sut.Reset();

        sut.IsReady.Should().BeFalse();
        sut.Calculate().Should().BeNull();
        sut.LastVolume.Should().Be(0m);
    }

    [Fact]
    public void SerializeDeserialize_RoundTrips()
    {
        var sut = new VolumeSmaIndicator(5);
        for (var i = 0; i < 5; i++)
            sut.UpdateVolume(100m * (i + 1));

        var json = sut.SerializeState();

        var restored = new VolumeSmaIndicator(5);
        var result = restored.DeserializeState(json);

        result.Should().BeTrue();
        restored.IsReady.Should().BeTrue();
        restored.Calculate().Should().Be(sut.Calculate());
    }

    [Fact]
    public void DeserializeState_WhenPeriodMismatch_ReturnsFalse()
    {
        var sut = new VolumeSmaIndicator(5);
        for (var i = 0; i < 5; i++)
            sut.UpdateVolume(100m);

        var json = sut.SerializeState();

        var different = new VolumeSmaIndicator(10);
        different.DeserializeState(json).Should().BeFalse();
    }
}
