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

        position.UnrealizedPnL.Should().Be(5000m);
        position.UnrealizedPnLPercent.Should().Be(10m);
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
        result.Value.Should().Be(2000m);
        position.IsOpen.Should().BeFalse();
        position.RealizedPnL.Should().Be(2000m);
        position.ClosedAt.Should().NotBeNull();
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
}
