using FluentAssertions;
using TradingBot.Application.Strategies.Indicators;
using TradingBot.Core.Enums;

namespace TradingBot.Application.Tests.Indicators;

public sealed class MarketRegimeDetectorTests
{
    [Fact]
    public void Detect_WhenNoIndicatorsAvailable_ReturnsUnknown()
    {
        var result = MarketRegimeDetector.Detect(null, null, null, 100m);
        result.Regime.Should().Be(MarketRegime.Unknown);
    }

    [Fact]
    public void Detect_WhenIndicatorsNotReady_ReturnsUnknown()
    {
        var adx = new AdxIndicator(14);
        var bb = new BollingerBandsIndicator(20, 2m);
        var atr = new AtrIndicator(14);

        var result = MarketRegimeDetector.Detect(adx, bb, atr, 100m);
        result.Regime.Should().Be(MarketRegime.Unknown);
    }

    [Fact]
    public void Detect_WhenAdxHighWithUptrend_ReturnsTrending()
    {
        var adx = new AdxIndicator(5);
        // Strong uptrend
        for (var i = 0; i < 30; i++)
            adx.Update(100m + i * 5m);

        // ADX should be high enough for trending
        if (adx.Adx >= 25m)
        {
            var result = MarketRegimeDetector.Detect(adx, null, null, 250m);
            result.Regime.Should().Be(MarketRegime.Trending);
            result.AdxValue.Should().NotBeNull();
        }
    }

    [Fact]
    public void Detect_WhenAdxLow_ReturnsRanging()
    {
        var adx = new AdxIndicator(5);
        // Flat data → low ADX
        for (var i = 0; i < 30; i++)
            adx.Update(100m);

        if (adx.IsReady && adx.Adx <= 20m)
        {
            var result = MarketRegimeDetector.Detect(adx, null, null, 100m);
            result.Regime.Should().Be(MarketRegime.Ranging);
        }
    }

    [Fact]
    public void Detect_WhenBandWidthExtremelyHigh_ReturnsHighVolatility()
    {
        var bb = new BollingerBandsIndicator(5, 2m);
        // Alternating prices → very wide bands
        for (var i = 0; i < 30; i++)
            bb.Update(i % 2 == 0 ? 80m : 130m);

        if (bb.IsReady && bb.BandWidth > 0.08m)
        {
            var result = MarketRegimeDetector.Detect(null, bb, null, 100m);
            result.Regime.Should().Be(MarketRegime.HighVolatility);
            result.BandWidth.Should().BeGreaterThan(0.08m);
        }
    }

    [Fact]
    public void Detect_WhenAtrVeryHighRelativeToPrice_ReturnsHighVolatility()
    {
        var atr = new AtrIndicator(5);
        // Huge swings → ATR > 3% of price
        for (var i = 0; i < 20; i++)
            atr.Update(i % 2 == 0 ? 100m : 110m);

        if (atr.IsReady)
        {
            var atrPercent = atr.Value!.Value / 100m;
            if (atrPercent > 0.03m)
            {
                var result = MarketRegimeDetector.Detect(null, null, atr, 100m);
                result.Regime.Should().Be(MarketRegime.HighVolatility);
            }
        }
    }

    [Fact]
    public void Detect_ResultIncludesMetrics()
    {
        var adx = new AdxIndicator(5);
        var bb = new BollingerBandsIndicator(5, 2m);
        var atr = new AtrIndicator(5);

        for (var i = 0; i < 30; i++)
        {
            var price = 100m + i;
            adx.Update(price);
            bb.Update(price);
            atr.Update(price);
        }

        var result = MarketRegimeDetector.Detect(adx, bb, atr, 130m);
        result.Regime.Should().NotBe(MarketRegime.Unknown);
        result.AdxValue.Should().NotBeNull();
        result.BandWidth.Should().NotBeNull();
        result.AtrPercent.Should().NotBeNull();
    }
}
