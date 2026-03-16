using FluentAssertions;
using TradingBot.Core.Entities;
using TradingBot.Core.Enums;
using TradingBot.Core.ValueObjects;

namespace TradingBot.Core.Tests.Entities;

public sealed class PositionTests
{
    private static Position CreateOpenPosition(
        decimal entryPrice = 50000m,
        decimal quantity   = 0.1m,
        OrderSide side     = OrderSide.Buy)
    {
        return Position.Open(
            Guid.NewGuid(),
            Symbol.Create("BTCUSDT").Value,
            side,
            Price.Create(entryPrice).Value,
            Quantity.Create(quantity).Value);
    }

    [Fact]
    public void Open_CreatesOpenPosition()
    {
        var position = CreateOpenPosition();

        position.IsOpen.Should().BeTrue();
        position.RealizedPnL.Should().BeNull();
        position.ClosedAt.Should().BeNull();
    }

    [Fact]
    public void UnrealizedPnL_LongPosition_WhenPriceUp_IsPositive()
    {
        var position = CreateOpenPosition(entryPrice: 50000m, quantity: 1m);

        position.UpdatePrice(Price.Create(55000m).Value);

        // Gross = 5000, no fees on entry (default 0) → estimated exit fee = 0
        position.UnrealizedPnL.Should().Be(5000m);
        position.UnrealizedPnLPercent.Should().Be(10m);
    }

    [Fact]
    public void UnrealizedPnL_WithEntryFee_SubtractsEstimatedFees()
    {
        var position = Position.Open(
            Guid.NewGuid(),
            Symbol.Create("BTCUSDT").Value,
            OrderSide.Buy,
            Price.Create(50000m).Value,
            Quantity.Create(1m).Value,
            entryFee: 50m);

        position.UpdatePrice(Price.Create(55000m).Value);

        // Gross = 5000, minus entryFee(50) + estimatedExitFee(50) = 4900
        position.UnrealizedPnL.Should().Be(4900m);
    }

    [Fact]
    public void UnrealizedPnL_LongPosition_WhenPriceDown_IsNegative()
    {
        var position = CreateOpenPosition(entryPrice: 50000m, quantity: 1m);

        position.UpdatePrice(Price.Create(45000m).Value);

        position.UnrealizedPnL.Should().Be(-5000m);
    }

    [Fact]
    public void Close_ReturnsRealizedPnL()
    {
        var position = CreateOpenPosition(entryPrice: 50000m, quantity: 1m);

        var result = position.Close(Price.Create(52000m).Value);

        result.IsSuccess.Should().BeTrue();
        // Gross PnL = (52000 - 50000) * 1 = 2000, no fees
        result.Value.Should().Be(2000m);
        position.IsOpen.Should().BeFalse();
        position.RealizedPnL.Should().Be(2000m);
        position.ClosedAt.Should().NotBeNull();
    }

    [Fact]
    public void Close_WithFees_ReturnsNetRealizedPnL()
    {
        var position = Position.Open(
            Guid.NewGuid(),
            Symbol.Create("BTCUSDT").Value,
            OrderSide.Buy,
            Price.Create(50000m).Value,
            Quantity.Create(1m).Value,
            entryFee: 50m); // 0.1% de 50000

        var result = position.Close(Price.Create(52000m).Value, exitFee: 52m);

        result.IsSuccess.Should().BeTrue();
        // Gross = 2000, Net = 2000 - 50 - 52 = 1898
        result.Value.Should().Be(1898m);
        position.EntryFee.Should().Be(50m);
        position.ExitFee.Should().Be(52m);
    }

    [Fact]
    public void Close_WithFees_LosingTradeBecomesBiggerLoss()
    {
        var position = Position.Open(
            Guid.NewGuid(),
            Symbol.Create("BTCUSDT").Value,
            OrderSide.Buy,
            Price.Create(50000m).Value,
            Quantity.Create(1m).Value,
            entryFee: 50m);

        var result = position.Close(Price.Create(49900m).Value, exitFee: 49.9m);

        result.IsSuccess.Should().BeTrue();
        // Gross = -100, Net = -100 - 50 - 49.9 = -199.9
        result.Value.Should().Be(-199.9m);
    }

    [Fact]
    public void Close_AlreadyClosed_ReturnsFailure()
    {
        var position = CreateOpenPosition();
        position.Close(Price.Create(51000m).Value);

        var result = position.Close(Price.Create(52000m).Value);

        result.IsFailure.Should().BeTrue();
    }

    [Fact]
    public void UpdatePrice_WhenClosed_DoesNotUpdate()
    {
        var position = CreateOpenPosition(entryPrice: 50000m);
        position.Close(Price.Create(51000m).Value);
        var priceAfterClose = position.CurrentPrice;

        position.UpdatePrice(Price.Create(99999m).Value);

        position.CurrentPrice.Should().Be(priceAfterClose);
    }

    // ── Peak price tracking ───────────────────────────────────────────────

    [Fact]
    public void UpdatePrice_TracksHighestPrice()
    {
        var position = CreateOpenPosition(entryPrice: 100m);

        position.UpdatePrice(Price.Create(110m).Value);
        position.UpdatePrice(Price.Create(105m).Value);
        position.UpdatePrice(Price.Create(120m).Value);
        position.UpdatePrice(Price.Create(115m).Value);

        position.HighestPriceSinceEntry.Value.Should().Be(120m);
    }

    [Fact]
    public void UpdatePrice_TracksLowestPrice()
    {
        var position = CreateOpenPosition(entryPrice: 100m);

        position.UpdatePrice(Price.Create(95m).Value);
        position.UpdatePrice(Price.Create(98m).Value);
        position.UpdatePrice(Price.Create(90m).Value);
        position.UpdatePrice(Price.Create(92m).Value);

        position.LowestPriceSinceEntry.Value.Should().Be(90m);
    }
}
