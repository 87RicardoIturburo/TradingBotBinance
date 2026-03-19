using FluentAssertions;
using TradingBot.Application.Strategies.Indicators;
using TradingBot.Core.Enums;

namespace TradingBot.Application.Tests.Indicators;

public sealed class BollingerBandsIndicatorTests
{
    // ── Properties ────────────────────────────────────────────────────────

    [Fact]
    public void Type_ReturnsBollingerBands()
    {
        var sut = new BollingerBandsIndicator();

        sut.Type.Should().Be(IndicatorType.BollingerBands);
    }

    [Fact]
    public void Name_IncludesPeriodAndStdDev()
    {
        var sut = new BollingerBandsIndicator(20, 2m);

        sut.Name.Should().Be("BB(20,2.0)");
    }

    // ── Constructor validation ────────────────────────────────────────────

    [Fact]
    public void Constructor_WhenPeriodTooSmall_Throws()
    {
        var act = () => new BollingerBandsIndicator(period: 1);

        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void Constructor_WhenStdDevZero_Throws()
    {
        var act = () => new BollingerBandsIndicator(period: 20, stdDevMultiplier: 0m);

        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void Constructor_WhenStdDevNegative_Throws()
    {
        var act = () => new BollingerBandsIndicator(period: 20, stdDevMultiplier: -1m);

        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    // ── IsReady ───────────────────────────────────────────────────────────

    [Fact]
    public void IsReady_WhenNotEnoughData_ReturnsFalse()
    {
        var sut = new BollingerBandsIndicator(5, 2m);

        for (var i = 0; i < 4; i++)
            sut.Update(100m);

        sut.IsReady.Should().BeFalse();
    }

    [Fact]
    public void IsReady_WhenEnoughData_ReturnsTrue()
    {
        var sut = new BollingerBandsIndicator(5, 2m);

        for (var i = 0; i < 5; i++)
            sut.Update(100m);

        sut.IsReady.Should().BeTrue();
    }

    // ── Calculate (Middle Band = SMA) ─────────────────────────────────────

    [Fact]
    public void Calculate_WhenNotReady_ReturnsNull()
    {
        var sut = new BollingerBandsIndicator(5, 2m);
        sut.Update(100m);

        sut.Calculate().Should().BeNull();
    }

    [Fact]
    public void Calculate_WithFlatPrices_ReturnsSamePrice()
    {
        var sut = new BollingerBandsIndicator(5, 2m);

        for (var i = 0; i < 5; i++)
            sut.Update(100m);

        sut.Calculate().Should().Be(100m);
    }

    [Fact]
    public void Calculate_ReturnsSmaOfPrices()
    {
        var sut = new BollingerBandsIndicator(5, 2m);
        var prices = new[] { 10m, 20m, 30m, 40m, 50m };

        foreach (var price in prices)
            sut.Update(price);

        sut.Calculate().Should().Be(30m); // (10+20+30+40+50)/5
    }

    // ── Bands ─────────────────────────────────────────────────────────────

    [Fact]
    public void Bands_WhenNotReady_ReturnNull()
    {
        var sut = new BollingerBandsIndicator(5, 2m);
        sut.Update(100m);

        sut.UpperBand.Should().BeNull();
        sut.MiddleBand.Should().BeNull();
        sut.LowerBand.Should().BeNull();
    }

    [Fact]
    public void Bands_WithFlatPrices_UpperAndLowerEqualMiddle()
    {
        var sut = new BollingerBandsIndicator(5, 2m);

        for (var i = 0; i < 5; i++)
            sut.Update(100m);

        // StdDev = 0, so all bands = SMA
        sut.MiddleBand.Should().Be(100m);
        sut.UpperBand.Should().Be(100m);
        sut.LowerBand.Should().Be(100m);
    }

    [Fact]
    public void Bands_UpperAlwaysGreaterThanOrEqualMiddle()
    {
        var sut = new BollingerBandsIndicator(5, 2m);
        var prices = new[] { 100m, 102m, 98m, 105m, 95m };

        foreach (var price in prices)
            sut.Update(price);

        sut.UpperBand.Should().BeGreaterThanOrEqualTo(sut.MiddleBand!.Value);
    }

    [Fact]
    public void Bands_LowerAlwaysLessThanOrEqualMiddle()
    {
        var sut = new BollingerBandsIndicator(5, 2m);
        var prices = new[] { 100m, 102m, 98m, 105m, 95m };

        foreach (var price in prices)
            sut.Update(price);

        sut.LowerBand.Should().BeLessThanOrEqualTo(sut.MiddleBand!.Value);
    }

    [Fact]
    public void Bands_AreSymmetricAroundMiddle()
    {
        var sut = new BollingerBandsIndicator(5, 2m);
        var prices = new[] { 100m, 102m, 98m, 105m, 95m };

        foreach (var price in prices)
            sut.Update(price);

        var middle = sut.MiddleBand!.Value;
        var upperDistance = sut.UpperBand!.Value - middle;
        var lowerDistance = middle - sut.LowerBand!.Value;

        upperDistance.Should().BeApproximately(lowerDistance, 0.0001m);
    }

    [Fact]
    public void Bands_WithHigherStdDev_AreBroader()
    {
        var narrow = new BollingerBandsIndicator(5, 1m);
        var wide   = new BollingerBandsIndicator(5, 3m);
        var prices = new[] { 100m, 102m, 98m, 105m, 95m };

        foreach (var price in prices)
        {
            narrow.Update(price);
            wide.Update(price);
        }

        var narrowWidth = narrow.UpperBand!.Value - narrow.LowerBand!.Value;
        var wideWidth   = wide.UpperBand!.Value - wide.LowerBand!.Value;

        wideWidth.Should().BeGreaterThan(narrowWidth);
    }

    // ── BandWidth ─────────────────────────────────────────────────────────

    [Fact]
    public void BandWidth_WhenNotReady_ReturnsNull()
    {
        var sut = new BollingerBandsIndicator(5, 2m);
        sut.Update(100m);

        sut.BandWidth.Should().BeNull();
    }

    [Fact]
    public void BandWidth_WithFlatPrices_ReturnsZero()
    {
        var sut = new BollingerBandsIndicator(5, 2m);

        for (var i = 0; i < 5; i++)
            sut.Update(100m);

        sut.BandWidth.Should().Be(0m);
    }

    [Fact]
    public void BandWidth_WithVolatilePrices_ReturnsPositive()
    {
        var sut = new BollingerBandsIndicator(5, 2m);
        var prices = new[] { 100m, 110m, 90m, 115m, 85m };

        foreach (var price in prices)
            sut.Update(price);

        sut.BandWidth.Should().BeGreaterThan(0m);
    }

    // ── Sliding window ────────────────────────────────────────────────────

    [Fact]
    public void Update_MaintainsSlidingWindow()
    {
        var sut = new BollingerBandsIndicator(3, 2m);

        sut.Update(10m);
        sut.Update(20m);
        sut.Update(30m);
        sut.Calculate().Should().Be(20m); // (10+20+30)/3

        sut.Update(40m);
        sut.Calculate().Should().Be(30m); // (20+30+40)/3 — 10 dropped
    }

    // ── Reset ─────────────────────────────────────────────────────────────

    [Fact]
    public void Reset_ClearsState()
    {
        var sut = new BollingerBandsIndicator(5, 2m);

        for (var i = 0; i < 5; i++)
            sut.Update(100m + i);

        sut.IsReady.Should().BeTrue();

        sut.Reset();

        sut.IsReady.Should().BeFalse();
        sut.Calculate().Should().BeNull();
        sut.UpperBand.Should().BeNull();
        sut.LowerBand.Should().BeNull();
        sut.BandWidth.Should().BeNull();
    }

    // ── Squeeze detection ────────────────────────────────────────────────

    [Fact]
    public void IsSqueezing_WhenNotEnoughHistory_ReturnsFalse()
    {
        var sut = new BollingerBandsIndicator(5, 2m);

        for (var i = 0; i < 5; i++)
            sut.Update(100m);

        sut.IsSqueezing.Should().BeFalse();
    }

    [Fact]
    public void IsSqueezing_WhenBandWidthCompresses_ReturnsTrue()
    {
        var sut = new BollingerBandsIndicator(5, 2m);

        // Fase 1: volatilidad alta para llenar el historial de BandWidth
        for (var i = 0; i < 25; i++)
            sut.Update(100m + (i % 2 == 0 ? 10m : -10m));

        // Fase 2: precios planos → BandWidth se comprime
        for (var i = 0; i < 10; i++)
            sut.Update(100m);

        sut.IsSqueezing.Should().BeTrue();
    }

    [Fact]
    public void SqueezeReleased_WhenBandWidthExpandsAfterSqueeze_ReturnsTrue()
    {
        var sut = new BollingerBandsIndicator(5, 2m);

        // Fase 1: volatilidad alta
        for (var i = 0; i < 25; i++)
            sut.Update(100m + (i % 2 == 0 ? 10m : -10m));

        // Fase 2: compresión (precios planos)
        for (var i = 0; i < 10; i++)
            sut.Update(100m);

        sut.IsSqueezing.Should().BeTrue();

        // Fase 3: una sola expansión (breakout) — verificar inmediatamente
        sut.Update(120m);

        sut.SqueezeReleased.Should().BeTrue();
    }

    [Fact]
    public void SqueezeReleased_WhenNoSqueezeBefore_ReturnsFalse()
    {
        var sut = new BollingerBandsIndicator(5, 2m);

        // BandWidth creciente progresivo — nunca hubo squeeze
        for (var i = 0; i < 30; i++)
            sut.Update(100m + i * 2m);

        sut.SqueezeReleased.Should().BeFalse();
    }
}
