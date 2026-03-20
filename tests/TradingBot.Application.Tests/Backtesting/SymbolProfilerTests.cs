using FluentAssertions;
using TradingBot.Application.Backtesting;
using TradingBot.Core.Interfaces.Services;

namespace TradingBot.Application.Tests.Backtesting;

public sealed class SymbolProfilerTests
{
    [Fact]
    public void Analyze_WhenInsufficientKlines_ReturnsDefaults()
    {
        var klines = GenerateKlines(10, 100m, 0.02m);

        var profile = SymbolProfiler.Analyze(klines);

        profile.MedianAtrPercent.Should().Be(0.03m);
        profile.MedianBandWidth.Should().Be(0.08m);
        profile.AdjustedHighVolatilityAtrPercent.Should().Be(0.06m);
    }

    [Fact]
    public void Analyze_WhenStablePrice_ReturnsLowVolatility()
    {
        var klines = GenerateKlines(100, 50000m, 0.005m);

        var profile = SymbolProfiler.Analyze(klines);

        profile.MedianAtrPercent.Should().BeGreaterThan(0m);
        profile.MedianAtrPercent.Should().BeLessThan(0.02m);
        profile.AdjustedHighVolatilityAtrPercent.Should().BeLessThan(0.04m);
    }

    [Fact]
    public void Analyze_WhenVolatilePrice_ReturnsHigherThresholds()
    {
        var klines = GenerateKlines(100, 0.50m, 0.10m);

        var profile = SymbolProfiler.Analyze(klines);

        profile.MedianAtrPercent.Should().BeGreaterThan(0.03m);
        profile.AdjustedHighVolatilityAtrPercent.Should().BeGreaterThan(0.06m);
    }

    [Fact]
    public void Analyze_AdjustedThresholdsAreDoubleMedian()
    {
        var klines = GenerateKlines(100, 1000m, 0.03m);

        var profile = SymbolProfiler.Analyze(klines);

        profile.AdjustedHighVolatilityAtrPercent.Should().BeApproximately(
            profile.MedianAtrPercent * 2m, 0.001m);
        profile.AdjustedHighVolatilityBandWidthPercent.Should().BeApproximately(
            profile.MedianBandWidth * 2m, 0.01m);
    }

    [Fact]
    public void Analyze_SpreadAdjustment_IsAtLeast01Percent()
    {
        var klines = GenerateKlines(50, 100m, 0.01m);

        var profile = SymbolProfiler.Analyze(klines, currentSpreadPercent: 0.01m);

        profile.AdjustedMaxSpreadPercent.Should().BeGreaterThanOrEqualTo(0.1m);
    }

    [Fact]
    public void Analyze_SpreadAdjustment_Is3xCurrentWhenLarge()
    {
        var klines = GenerateKlines(50, 100m, 0.01m);

        var profile = SymbolProfiler.Analyze(klines, currentSpreadPercent: 0.5m);

        profile.AdjustedMaxSpreadPercent.Should().Be(1.5m);
    }

    [Fact]
    public void Analyze_ConsistentVolume_ReturnsLowMinRatio()
    {
        var klines = GenerateKlinesWithConsistentVolume(100, 50000m);

        var profile = SymbolProfiler.Analyze(klines);

        profile.VolumeCV.Should().BeLessThan(0.5m);
        profile.AdjustedVolumeMinRatio.Should().Be(1.3m);
    }

    [Fact]
    public void Analyze_ErraticVolume_ReturnsHighMinRatio()
    {
        var klines = GenerateKlinesWithErraticVolume(100, 50000m);

        var profile = SymbolProfiler.Analyze(klines);

        profile.VolumeCV.Should().BeGreaterThan(1.0m);
        profile.AdjustedVolumeMinRatio.Should().Be(2.0m);
    }

    [Fact]
    public void StrategyTemplateStore_HasAllTemplates()
    {
        StrategyTemplateStore.All.Should().HaveCountGreaterThanOrEqualTo(7);
        StrategyTemplateStore.All.Select(t => t.Id).Should().OnlyHaveUniqueItems();
    }

    private static List<Kline> GenerateKlines(int count, decimal basePrice, decimal volatility)
    {
        var klines = new List<Kline>();
        var rng = new Random(42);
        var price = basePrice;

        for (var i = 0; i < count; i++)
        {
            var change = price * volatility * (decimal)(rng.NextDouble() * 2 - 1);
            var open = price;
            var close = price + change;
            var high = Math.Max(open, close) * (1m + volatility * 0.5m);
            var low = Math.Min(open, close) * (1m - volatility * 0.5m);
            if (low <= 0) low = 0.01m;
            if (close <= 0) close = 0.01m;

            klines.Add(new Kline(
                DateTimeOffset.UtcNow.AddHours(-count + i),
                open, high, low, close,
                1000m + (decimal)(rng.NextDouble() * 200)));

            price = close;
        }

        return klines;
    }

    private static List<Kline> GenerateKlinesWithConsistentVolume(int count, decimal basePrice)
    {
        var klines = new List<Kline>();
        var rng = new Random(42);

        for (var i = 0; i < count; i++)
        {
            var change = basePrice * 0.01m * (decimal)(rng.NextDouble() * 2 - 1);
            var close = basePrice + change;

            klines.Add(new Kline(
                DateTimeOffset.UtcNow.AddHours(-count + i),
                basePrice, close + 50m, close - 50m, close,
                1000m + (decimal)(rng.NextDouble() * 100)));
        }

        return klines;
    }

    private static List<Kline> GenerateKlinesWithErraticVolume(int count, decimal basePrice)
    {
        var klines = new List<Kline>();
        var rng = new Random(42);

        for (var i = 0; i < count; i++)
        {
            var change = basePrice * 0.01m * (decimal)(rng.NextDouble() * 2 - 1);
            var close = basePrice + change;
            var volume = i % 5 == 0 ? 10000m : 100m;

            klines.Add(new Kline(
                DateTimeOffset.UtcNow.AddHours(-count + i),
                basePrice, close + 50m, close - 50m, close,
                volume));
        }

        return klines;
    }
}
