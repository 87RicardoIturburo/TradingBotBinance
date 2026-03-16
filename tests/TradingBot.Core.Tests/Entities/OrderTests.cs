using FluentAssertions;
using TradingBot.Core.Common;
using TradingBot.Core.Entities;
using TradingBot.Core.Enums;
using TradingBot.Core.ValueObjects;

namespace TradingBot.Core.Tests.Entities;

public sealed class OrderTests
{
    private static Order CreatePendingOrder(
        OrderType type = OrderType.Market,
        TradingMode mode = TradingMode.PaperTrading,
        decimal? limitPrice = null)
    {
        var symbol   = Symbol.Create("BTCUSDT").Value;
        var quantity = Quantity.Create(0.01m).Value;
        var limit    = limitPrice.HasValue ? Price.Create(limitPrice.Value).Value : null;

        return Order.Create(
            Guid.NewGuid(), symbol, OrderSide.Buy, type,
            quantity, mode, limit).Value;
    }

    [Fact]
    public void Create_MarketOrder_StatusIsPending()
    {
        var order = CreatePendingOrder();

        order.Status.Should().Be(OrderStatus.Pending);
        order.IsPaperTrade.Should().BeTrue();
        order.IsTerminal.Should().BeFalse();
    }

    [Fact]
    public void Create_LimitOrderWithoutPrice_ReturnsFailure()
    {
        var symbol   = Symbol.Create("BTCUSDT").Value;
        var quantity = Quantity.Create(0.01m).Value;

        var result = Order.Create(
            Guid.NewGuid(), symbol, OrderSide.Buy, OrderType.Limit,
            quantity, TradingMode.PaperTrading);

        result.IsFailure.Should().BeTrue();
    }

    [Fact]
    public void Submit_FromPending_TransitionsToSubmitted()
    {
        var order = CreatePendingOrder();

        var result = order.Submit("PAPER-123");

        result.IsSuccess.Should().BeTrue();
        order.Status.Should().Be(OrderStatus.Submitted);
        order.BinanceOrderId.Should().Be("PAPER-123");
        order.DomainEvents.Should().ContainSingle();
    }

    [Fact]
    public void Submit_FromSubmitted_ReturnsFailure()
    {
        var order = CreatePendingOrder();
        order.Submit();

        var result = order.Submit();

        result.IsFailure.Should().BeTrue();
    }

    [Fact]
    public void Fill_FromSubmitted_TransitionsToFilled()
    {
        var order = CreatePendingOrder();
        order.Submit();

        var qty   = Quantity.Create(0.01m).Value;
        var price = Price.Create(50000m).Value;
        var result = order.Fill(qty, price);

        result.IsSuccess.Should().BeTrue();
        order.Status.Should().Be(OrderStatus.Filled);
        order.IsTerminal.Should().BeTrue();
        order.FilledAt.Should().NotBeNull();
    }

    [Fact]
    public void Cancel_FromPending_TransitionsToCancelled()
    {
        var order = CreatePendingOrder();

        var result = order.Cancel("Test cancel");

        result.IsSuccess.Should().BeTrue();
        order.Status.Should().Be(OrderStatus.Cancelled);
        order.IsTerminal.Should().BeTrue();
    }

    [Fact]
    public void Cancel_FromFilled_ReturnsFailure()
    {
        var order = CreatePendingOrder();
        order.Submit();
        order.Fill(Quantity.Create(0.01m).Value, Price.Create(50000m).Value);

        var result = order.Cancel("Too late");

        result.IsFailure.Should().BeTrue();
    }

    [Fact]
    public void PartialFill_FromSubmitted_TransitionsToPartiallyFilled()
    {
        var order = CreatePendingOrder();
        order.Submit();

        var result = order.PartialFill(
            Quantity.Create(0.005m).Value,
            Price.Create(50000m).Value);

        result.IsSuccess.Should().BeTrue();
        order.Status.Should().Be(OrderStatus.PartiallyFilled);
    }

    [Fact]
    public void Reject_FromSubmitted_TransitionsToRejected()
    {
        var order = CreatePendingOrder();
        order.Submit();

        var result = order.Reject("Insufficient balance");

        result.IsSuccess.Should().BeTrue();
        order.Status.Should().Be(OrderStatus.Rejected);
        order.IsTerminal.Should().BeTrue();
    }

    // ── NotionalValue ─────────────────────────────────────────────────────

    [Fact]
    public void NotionalValue_WhenLimitOrder_UsesLimitPrice()
    {
        var order = CreatePendingOrder(OrderType.Limit, limitPrice: 50000m);

        order.NotionalValue.Should().Be(0.01m * 50000m);
    }

    [Fact]
    public void NotionalValue_WhenMarketOrderWithEstimatedPrice_UsesEstimatedPrice()
    {
        var symbol = Symbol.Create("BTCUSDT").Value;
        var qty    = Quantity.Create(0.01m).Value;
        var est    = Price.Create(55000m).Value;

        var order = Order.Create(
            Guid.NewGuid(), symbol, OrderSide.Buy, OrderType.Market,
            qty, TradingMode.PaperTrading, estimatedPrice: est).Value;

        order.NotionalValue.Should().Be(0.01m * 55000m);
    }

    [Fact]
    public void NotionalValue_WhenMarketOrderNoPrice_ReturnsZero()
    {
        var order = CreatePendingOrder();

        order.NotionalValue.Should().Be(0m);
    }

    // ── AdjustForExchange ─────────────────────────────────────────────────

    [Fact]
    public void AdjustForExchange_UpdatesQuantity()
    {
        var order = CreatePendingOrder();

        order.AdjustForExchange(0.005m, null);

        order.Quantity.Value.Should().Be(0.005m);
    }

    [Fact]
    public void AdjustForExchange_UpdatesLimitPrice()
    {
        var order = CreatePendingOrder(OrderType.Limit, limitPrice: 50000m);

        order.AdjustForExchange(0.01m, 49999.50m);

        order.LimitPrice!.Value.Should().Be(49999.50m);
    }
}
