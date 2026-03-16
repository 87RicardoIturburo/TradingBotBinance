using FluentAssertions;
using TradingBot.Core.ValueObjects;

namespace TradingBot.Core.Tests.ValueObjects;

public sealed class ExchangeSymbolFiltersTests
{
    private static ExchangeSymbolFilters CreateFilters(
        decimal minQty      = 0.00001m,
        decimal maxQty      = 9000m,
        decimal stepSize    = 0.00001m,
        decimal tickSize    = 0.01m,
        decimal minNotional = 10m,
        int     maxOrders   = 200)
        => new("BTCUSDT", minQty, maxQty, stepSize, tickSize, minNotional, maxOrders);

    // ── AdjustQuantity ────────────────────────────────────────────────────

    [Fact]
    public void AdjustQuantity_RoundsDownToStepSize()
    {
        var filters = CreateFilters(stepSize: 0.001m);

        filters.AdjustQuantity(1.2345m).Should().Be(1.234m);
    }

    [Fact]
    public void AdjustQuantity_WhenExactMultiple_ReturnsUnchanged()
    {
        var filters = CreateFilters(stepSize: 0.01m);

        filters.AdjustQuantity(1.23m).Should().Be(1.23m);
    }

    [Fact]
    public void AdjustQuantity_WhenStepSizeZero_ReturnsOriginal()
    {
        var filters = CreateFilters(stepSize: 0m);

        filters.AdjustQuantity(1.23456789m).Should().Be(1.23456789m);
    }

    [Fact]
    public void AdjustQuantity_WhenVerySmallQuantity_RoundsToZero()
    {
        var filters = CreateFilters(stepSize: 0.01m);

        filters.AdjustQuantity(0.005m).Should().Be(0m);
    }

    // ── AdjustPrice ───────────────────────────────────────────────────────

    [Fact]
    public void AdjustPrice_RoundsToTickSize()
    {
        var filters = CreateFilters(tickSize: 0.01m);

        filters.AdjustPrice(55000.123m).Should().Be(55000.12m);
    }

    [Fact]
    public void AdjustPrice_RoundsHalfUp()
    {
        var filters = CreateFilters(tickSize: 0.01m);

        filters.AdjustPrice(55000.125m).Should().Be(55000.13m);
    }

    [Fact]
    public void AdjustPrice_WhenTickSizeZero_ReturnsOriginal()
    {
        var filters = CreateFilters(tickSize: 0m);

        filters.AdjustPrice(55000.123456m).Should().Be(55000.123456m);
    }

    [Fact]
    public void AdjustPrice_WhenExactMultiple_ReturnsUnchanged()
    {
        var filters = CreateFilters(tickSize: 0.01m);

        filters.AdjustPrice(55000.12m).Should().Be(55000.12m);
    }

    // ── ValidateAndAdjust ─────────────────────────────────────────────────

    [Fact]
    public void ValidateAndAdjust_WhenValidQuantity_ReturnsSuccess()
    {
        var filters = CreateFilters();

        var result = filters.ValidateAndAdjust(0.01m, null);

        result.IsSuccess.Should().BeTrue();
        result.Value.Quantity.Should().Be(0.01m);
    }

    [Fact]
    public void ValidateAndAdjust_AdjustsQuantityAndPrice()
    {
        var filters = CreateFilters(stepSize: 0.001m, tickSize: 0.01m);

        var result = filters.ValidateAndAdjust(1.2345m, 55000.567m);

        result.IsSuccess.Should().BeTrue();
        result.Value.Quantity.Should().Be(1.234m);
        result.Value.Price.Should().Be(55000.57m);
    }

    [Fact]
    public void ValidateAndAdjust_WhenQuantityBecomesZero_ReturnsFailure()
    {
        var filters = CreateFilters(stepSize: 1m);

        var result = filters.ValidateAndAdjust(0.5m, null);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("VALIDATION_ERROR");
        result.Error.Message.Should().Contain("cero o negativa");
    }

    [Fact]
    public void ValidateAndAdjust_WhenBelowMinQty_ReturnsFailure()
    {
        var filters = CreateFilters(minQty: 0.1m, stepSize: 0.00001m);

        var result = filters.ValidateAndAdjust(0.05m, null);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("VALIDATION_ERROR");
        result.Error.Message.Should().Contain("mínimo permitido");
    }

    [Fact]
    public void ValidateAndAdjust_WhenAboveMaxQty_ReturnsFailure()
    {
        var filters = CreateFilters(maxQty: 0.5m);

        var result = filters.ValidateAndAdjust(1.0m, null);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("VALIDATION_ERROR");
        result.Error.Message.Should().Contain("máximo permitido");
    }

    [Fact]
    public void ValidateAndAdjust_WhenNotionalBelowMinimum_ReturnsFailure()
    {
        // qty=0.001 * price=5 = 0.005 USDT < minNotional=10
        var filters = CreateFilters(minNotional: 10m);

        var result = filters.ValidateAndAdjust(0.001m, 5m);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("VALIDATION_ERROR");
        result.Error.Message.Should().Contain("Notional");
    }

    [Fact]
    public void ValidateAndAdjust_WhenNotionalAboveMinimum_ReturnsSuccess()
    {
        // qty=0.01 * price=55000 = 550 USDT > minNotional=10
        var filters = CreateFilters(minNotional: 10m);

        var result = filters.ValidateAndAdjust(0.01m, 55000m);

        result.IsSuccess.Should().BeTrue();
    }

    [Fact]
    public void ValidateAndAdjust_WhenNullPrice_SkipsNotionalValidation()
    {
        var filters = CreateFilters(minNotional: 10m);

        // Market order: no limit price, so notional can't be checked
        var result = filters.ValidateAndAdjust(0.001m, null);

        result.IsSuccess.Should().BeTrue();
        result.Value.Price.Should().BeNull();
    }

    [Fact]
    public void ValidateAndAdjust_WhenMaxQtyIsZero_SkipsMaxValidation()
    {
        var filters = CreateFilters(maxQty: 0m);

        var result = filters.ValidateAndAdjust(100000m, null);

        result.IsSuccess.Should().BeTrue();
    }

    // ── Precision ─────────────────────────────────────────────────────────

    [Fact]
    public void QuantityPrecision_MatchesStepSize()
    {
        var filters = CreateFilters(stepSize: 0.001m);

        filters.QuantityPrecision.Should().Be(3);
    }

    [Fact]
    public void PricePrecision_MatchesTickSize()
    {
        var filters = CreateFilters(tickSize: 0.01m);

        filters.PricePrecision.Should().Be(2);
    }

    [Fact]
    public void QuantityPrecision_WhenStepSizeZero_ReturnsDefault8()
    {
        var filters = CreateFilters(stepSize: 0m);

        filters.QuantityPrecision.Should().Be(8);
    }
}
