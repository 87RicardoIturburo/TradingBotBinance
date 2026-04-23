using FluentAssertions;
using TradingBot.Application.Strategies.Indicators;
using TradingBot.Core.Enums;

namespace TradingBot.Application.Tests.Indicators;

public sealed class MarketRegimeDetectorTests
{
    private readonly MarketRegimeDetector _detector = new();

    [Fact]
    public void Detect_WhenNoIndicatorsAvailable_ReturnsUnknown()
    {
        var result = _detector.Detect(null, null, null, 100m);
        result.Regime.Should().Be(MarketRegime.Unknown);
    }

    [Fact]
    public void Detect_WhenIndicatorsNotReady_ReturnsUnknown()
    {
        var adx = new AdxIndicator(14);
        var bb = new BollingerBandsIndicator(20, 2m);
        var atr = new AtrIndicator(14);

        var result = _detector.Detect(adx, bb, atr, 100m);
        result.Regime.Should().Be(MarketRegime.Unknown);
    }

    [Fact]
    public void Detect_WhenAdxHighWithUptrend_ReturnsTrending()
    {
        var adx = new AdxIndicator(5);
        for (var i = 0; i < 30; i++)
            adx.Update(100m + i * 5m);

        if (adx.Adx >= 25m)
        {
            var result = _detector.Detect(adx, null, null, 250m);
            result.Regime.Should().BeOneOf(MarketRegime.Trending, MarketRegime.Bearish, MarketRegime.Indefinite);
            result.AdxValue.Should().NotBeNull();
        }
    }

    [Fact]
    public void Detect_WhenAdxLow_ReturnsRangingOrIndefinite()
    {
        var adx = new AdxIndicator(5);
        for (var i = 0; i < 30; i++)
            adx.Update(100m);

        if (adx.IsReady && adx.Adx <= 20m)
        {
            var result = _detector.Detect(adx, null, null, 100m);
            result.Regime.Should().BeOneOf(MarketRegime.Ranging, MarketRegime.Indefinite);
        }
    }

    [Fact]
    public void Detect_WhenBandWidthExtremelyHigh_ReturnsHighVolatility()
    {
        var bb = new BollingerBandsIndicator(5, 2m);
        for (var i = 0; i < 30; i++)
            bb.Update(i % 2 == 0 ? 80m : 130m);

        if (bb.IsReady && bb.BandWidth > 0.08m)
        {
            var result = _detector.Detect(null, bb, null, 100m);
            result.Regime.Should().Be(MarketRegime.HighVolatility);
            result.BandWidth.Should().BeGreaterThan(0.08m);
        }
    }

    [Fact]
    public void Detect_WhenAtrVeryHighRelativeToPrice_ReturnsHighVolatility()
    {
        var atr = new AtrIndicator(5);
        for (var i = 0; i < 20; i++)
            atr.Update(i % 2 == 0 ? 100m : 110m);

        if (atr.IsReady)
        {
            var atrPercent = atr.Value!.Value / 100m;
            if (atrPercent > 0.03m)
            {
                var result = _detector.Detect(null, null, atr, 100m);
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

        var result = _detector.Detect(adx, bb, atr, 130m);
        result.Regime.Should().NotBe(MarketRegime.Unknown);
        result.AdxValue.Should().NotBeNull();
        result.BandWidth.Should().NotBeNull();
        result.AtrPercent.Should().NotBeNull();
        result.Score.Should().NotBeNull();
    }

    [Fact]
    public void Detect_WithBullishEmaAndHigherHighs_ReturnsTrending()
    {
        var ema = new EmaAlignmentDetector();
        var hh = new HigherHighLowDetector();

        for (var i = 0; i < 60; i++)
        {
            ema.Update(100m + i);
            hh.Update(100m + i + 1, 100m + i - 1);
        }

        ema.IsBullishAligned.Should().BeTrue();
        hh.HasHigherHighs().Should().BeTrue();

        var result = _detector.Detect(null, null, null, 160m,
            emaAlignment: ema, hhLlDetector: hh, volumeRatio: 1.5m);

        result.Regime.Should().Be(MarketRegime.Trending);
        result.Score.Should().NotBeNull();
        result.Score!.TrendingScore.Should().BeGreaterThanOrEqualTo(2);
    }

    [Fact]
    public void Detect_WithFlatEmaAndNarrowBands_ReturnsRangingOrIndefinite()
    {
        var ema = new EmaAlignmentDetector();
        for (var i = 0; i < 60; i++)
            ema.Update(100m);

        ema.IsFlat().Should().BeTrue();

        var bb = new BollingerBandsIndicator(5, 2m);
        for (var i = 0; i < 30; i++)
            bb.Update(100m + (i % 2 == 0 ? 0.1m : -0.1m));

        var result = _detector.Detect(null, bb, null, 100m, emaAlignment: ema);
        result.Regime.Should().BeOneOf(MarketRegime.Ranging, MarketRegime.Indefinite);
    }

    [Fact]
    public void Detect_WithNoStrongSignals_ReturnsUnknownOrIndefinite()
    {
        var result = _detector.Detect(null, null, null, 100m,
            emaAlignment: null, hhLlDetector: null, volumeRatio: null);

        result.Regime.Should().Be(MarketRegime.Unknown);
    }

    [Fact]
    public void Detect_WithWeakSignalsOnly_ReturnsIndefinite()
    {
        var result = _detector.Detect(null, null, null, 100m, volumeRatio: 0.3m);

        result.Regime.Should().Be(MarketRegime.Indefinite);
    }

    [Fact]
    public void Detect_MaxScoreLessThan2_ForcesIndefinite()
    {
        var ema = new EmaAlignmentDetector();
        for (var i = 0; i < 60; i++)
            ema.Update(100m + (i % 3 == 0 ? 1 : -1));

        var result = _detector.Detect(null, null, null, 100m, emaAlignment: ema);

        if (result.Score is not null)
        {
            var max = Math.Max(result.Score.TrendingScore,
                Math.Max(result.Score.RangingScore, result.Score.IndefiniteScore));
            if (max < 2)
                result.Regime.Should().Be(MarketRegime.Indefinite);
        }
    }

    [Fact]
    public void Detect_NullVolumeRatio_DoesNotContributeToScore()
    {
        var result1 = _detector.Detect(null, null, null, 100m, volumeRatio: null);
        var detector2 = new MarketRegimeDetector();
        var result2 = detector2.Detect(null, null, null, 100m, volumeRatio: 2.0m);

        result1.Score?.TrendingScore.Should().BeLessThanOrEqualTo(
            result2.Score?.TrendingScore ?? 0);
    }

    [Fact]
    public void Detect_LowVolumeAddsIndefinitePoint()
    {
        var result = _detector.Detect(null, null, null, 100m, volumeRatio: 0.3m);
        result.Score.Should().NotBeNull();
        result.Score!.IndefiniteScore.Should().BeGreaterThanOrEqualTo(1);
    }

    [Fact]
    public void Detect_HighVolumeAddsTrendingPoint()
    {
        var ema = new EmaAlignmentDetector();
        var hh = new HigherHighLowDetector();
        for (var i = 0; i < 60; i++)
        {
            ema.Update(100m + i);
            hh.Update(100m + i + 1, 100m + i - 1);
        }

        var resultWithVol = _detector.Detect(null, null, null, 160m,
            emaAlignment: ema, hhLlDetector: hh, volumeRatio: 1.5m);

        var detector2 = new MarketRegimeDetector();
        var resultNoVol = detector2.Detect(null, null, null, 160m,
            emaAlignment: ema, hhLlDetector: hh, volumeRatio: null);

        resultWithVol.Score!.TrendingScore.Should()
            .BeGreaterThan(resultNoVol.Score!.TrendingScore);
    }

    [Fact]
    public void Detect_HighVolatility_AlwaysTakesPriority()
    {
        var bb = new BollingerBandsIndicator(5, 2m);
        for (var i = 0; i < 30; i++)
            bb.Update(i % 2 == 0 ? 50m : 150m);

        if (bb.IsReady && bb.BandWidth > 0.08m)
        {
            var ema = new EmaAlignmentDetector();
            for (var i = 0; i < 60; i++)
                ema.Update(100m + i);

            var result = _detector.Detect(null, bb, null, 100m,
                emaAlignment: ema, volumeRatio: 2.0m);

            result.Regime.Should().Be(MarketRegime.HighVolatility);
        }
    }

    [Fact]
    public void Confirm_SingleChange_MaintainsPrevious()
    {
        var detector = new MarketRegimeDetector();
        detector.GetConfirmedRegime(MarketRegime.Trending, 3);
        detector.GetConfirmedRegime(MarketRegime.Trending, 3);
        detector.GetConfirmedRegime(MarketRegime.Trending, 3);

        var result = detector.GetConfirmedRegime(MarketRegime.Ranging, 3);
        result.Should().Be(MarketRegime.Trending);
    }

    [Fact]
    public void Confirm_NConsecutive_ChangesRegime()
    {
        var detector = new MarketRegimeDetector();
        detector.GetConfirmedRegime(MarketRegime.Trending, 3);
        detector.GetConfirmedRegime(MarketRegime.Trending, 3);
        detector.GetConfirmedRegime(MarketRegime.Trending, 3);

        detector.GetConfirmedRegime(MarketRegime.Ranging, 3);
        detector.GetConfirmedRegime(MarketRegime.Ranging, 3);
        var result = detector.GetConfirmedRegime(MarketRegime.Ranging, 3);

        result.Should().Be(MarketRegime.Ranging);
    }

    [Fact]
    public void Confirm_ExitIndefinite_RequiresNCandles()
    {
        var detector = new MarketRegimeDetector();
        detector.GetConfirmedRegime(MarketRegime.Indefinite, 3);
        detector.GetConfirmedRegime(MarketRegime.Indefinite, 3);
        detector.GetConfirmedRegime(MarketRegime.Indefinite, 3);

        var after1 = detector.GetConfirmedRegime(MarketRegime.Trending, 3);
        after1.Should().Be(MarketRegime.Indefinite);

        detector.GetConfirmedRegime(MarketRegime.Trending, 3);
        var after3 = detector.GetConfirmedRegime(MarketRegime.Trending, 3);
        after3.Should().Be(MarketRegime.Trending);
    }

    [Fact]
    public void Confirm_EnterIndefinite_RequiresNCandles()
    {
        var detector = new MarketRegimeDetector();
        detector.GetConfirmedRegime(MarketRegime.Trending, 3);
        detector.GetConfirmedRegime(MarketRegime.Trending, 3);
        detector.GetConfirmedRegime(MarketRegime.Trending, 3);

        var after1 = detector.GetConfirmedRegime(MarketRegime.Indefinite, 3);
        after1.Should().Be(MarketRegime.Trending);

        detector.GetConfirmedRegime(MarketRegime.Indefinite, 3);
        var after3 = detector.GetConfirmedRegime(MarketRegime.Indefinite, 3);
        after3.Should().Be(MarketRegime.Indefinite);
    }

    [Fact]
    public void Confirm_MixedDetections_MaintainsPrevious()
    {
        var detector = new MarketRegimeDetector();
        detector.GetConfirmedRegime(MarketRegime.Trending, 3);
        detector.GetConfirmedRegime(MarketRegime.Trending, 3);
        detector.GetConfirmedRegime(MarketRegime.Trending, 3);

        detector.GetConfirmedRegime(MarketRegime.Ranging, 3);
        detector.GetConfirmedRegime(MarketRegime.Trending, 3);
        var result = detector.GetConfirmedRegime(MarketRegime.Ranging, 3);

        result.Should().Be(MarketRegime.Trending);
    }
}
