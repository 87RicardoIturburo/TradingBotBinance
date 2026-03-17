using System.Diagnostics.Metrics;
using FluentAssertions;
using NSubstitute;
using TradingBot.Application.Diagnostics;

namespace TradingBot.Application.Tests.Diagnostics;

public sealed class TradingMetricsTests
{
    private readonly TradingMetrics _sut;

    public TradingMetricsTests()
    {
        var meterFactory = Substitute.For<IMeterFactory>();
        meterFactory.Create(Arg.Any<MeterOptions>()).Returns(new Meter("TradingBot.Test"));
        _sut = new TradingMetrics(meterFactory);
    }

    [Fact]
    public void GetSnapshot_WhenNoActivity_ReturnsAllZeros()
    {
        var snapshot = _sut.GetSnapshot();

        snapshot.TotalTicksProcessed.Should().Be(0);
        snapshot.TotalSignalsGenerated.Should().Be(0);
        snapshot.TotalOrdersPlaced.Should().Be(0);
        snapshot.TotalOrdersFailed.Should().Be(0);
        snapshot.TotalTicksDropped.Should().Be(0);
        snapshot.TotalOrdersPaper.Should().Be(0);
        snapshot.TotalOrdersLive.Should().Be(0);
        snapshot.LastLatencyMs.Should().Be(0);
        snapshot.AverageLatencyMs.Should().Be(0);
        snapshot.DailyPnLUsdt.Should().Be(0);
    }

    [Fact]
    public void GetSnapshot_AfterRecordingTicks_ReflectsCount()
    {
        _sut.RecordTickProcessed("BTCUSDT");
        _sut.RecordTickProcessed("BTCUSDT");
        _sut.RecordTickProcessed("ETHUSDT");

        var snapshot = _sut.GetSnapshot();

        snapshot.TotalTicksProcessed.Should().Be(3);
    }

    [Fact]
    public void GetSnapshot_AfterRecordingSignals_ReflectsCount()
    {
        _sut.RecordSignalGenerated("Strategy1", "BTCUSDT", "Buy");
        _sut.RecordSignalGenerated("Strategy1", "BTCUSDT", "Sell");

        var snapshot = _sut.GetSnapshot();

        snapshot.TotalSignalsGenerated.Should().Be(2);
    }

    [Fact]
    public void GetSnapshot_AfterRecordingOrders_SeparatesPaperAndLive()
    {
        _sut.RecordOrderPlaced("BTCUSDT", "Buy", "Market", isPaper: true);
        _sut.RecordOrderPlaced("BTCUSDT", "Sell", "Market", isPaper: true);
        _sut.RecordOrderPlaced("ETHUSDT", "Buy", "Limit", isPaper: false);

        var snapshot = _sut.GetSnapshot();

        snapshot.TotalOrdersPlaced.Should().Be(3);
        snapshot.TotalOrdersPaper.Should().Be(2);
        snapshot.TotalOrdersLive.Should().Be(1);
    }

    [Fact]
    public void GetSnapshot_AfterRecordingFailures_ReflectsCount()
    {
        _sut.RecordOrderFailed("BTCUSDT", "risk_validation");
        _sut.RecordOrderFailed("BTCUSDT", "exchange_rejected");

        var snapshot = _sut.GetSnapshot();

        snapshot.TotalOrdersFailed.Should().Be(2);
    }

    [Fact]
    public void GetSnapshot_AfterRecordingLatency_ReflectsLastAndAverage()
    {
        _sut.RecordTickToOrderLatency(10.0, "BTCUSDT");
        _sut.RecordTickToOrderLatency(30.0, "BTCUSDT");

        var snapshot = _sut.GetSnapshot();

        snapshot.LastLatencyMs.Should().Be(30.0);
        snapshot.AverageLatencyMs.Should().Be(20.0);
    }

    [Fact]
    public void GetSnapshot_AfterRecordingDrops_ReflectsCount()
    {
        _sut.RecordTickDropped("BTCUSDT", "ticker");
        _sut.RecordTickDropped("BTCUSDT", "kline");

        var snapshot = _sut.GetSnapshot();

        snapshot.TotalTicksDropped.Should().Be(2);
    }

    [Fact]
    public void GetSnapshot_AfterUpdatingDailyPnL_ReflectsValue()
    {
        _sut.UpdateDailyPnL(125.50);

        var snapshot = _sut.GetSnapshot();

        snapshot.DailyPnLUsdt.Should().Be(125.50);
    }

    [Fact]
    public void GetSnapshot_Timestamp_IsRecentUtc()
    {
        var before = DateTimeOffset.UtcNow;
        var snapshot = _sut.GetSnapshot();
        var after = DateTimeOffset.UtcNow;

        snapshot.Timestamp.Should().BeOnOrAfter(before);
        snapshot.Timestamp.Should().BeOnOrBefore(after);
    }
}
