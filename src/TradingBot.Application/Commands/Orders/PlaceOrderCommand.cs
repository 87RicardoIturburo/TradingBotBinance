using MediatR;
using TradingBot.Core.Common;
using TradingBot.Core.Entities;
using TradingBot.Core.Enums;
using TradingBot.Core.Interfaces.Services;
using TradingBot.Core.ValueObjects;

namespace TradingBot.Application.Commands.Orders;

/// <summary>Crea y ejecuta una orden a través del OrderService.</summary>
public sealed record PlaceOrderCommand(
    Guid        StrategyId,
    string      SymbolValue,
    OrderSide   Side,
    OrderType   Type,
    decimal     QuantityValue,
    TradingMode Mode,
    decimal?    LimitPriceValue = null,
    decimal?    StopPriceValue  = null) : IRequest<Result<Order, DomainError>>;

internal sealed class PlaceOrderCommandHandler(
    IOrderService orderService) : IRequestHandler<PlaceOrderCommand, Result<Order, DomainError>>
{
    public async Task<Result<Order, DomainError>> Handle(
        PlaceOrderCommand request,
        CancellationToken cancellationToken)
    {
        var symbolResult   = Symbol.Create(request.SymbolValue);
        if (symbolResult.IsFailure)
            return Result<Order, DomainError>.Failure(symbolResult.Error);

        var quantityResult = Quantity.Create(request.QuantityValue);
        if (quantityResult.IsFailure)
            return Result<Order, DomainError>.Failure(quantityResult.Error);

        Price? limitPrice = null;
        if (request.LimitPriceValue.HasValue)
        {
            var lp = Price.Create(request.LimitPriceValue.Value);
            if (lp.IsFailure) return Result<Order, DomainError>.Failure(lp.Error);
            limitPrice = lp.Value;
        }

        Price? stopPrice = null;
        if (request.StopPriceValue.HasValue)
        {
            var sp = Price.Create(request.StopPriceValue.Value);
            if (sp.IsFailure) return Result<Order, DomainError>.Failure(sp.Error);
            stopPrice = sp.Value;
        }

        var orderResult = Order.Create(
            request.StrategyId, symbolResult.Value, request.Side,
            request.Type, quantityResult.Value, request.Mode,
            limitPrice, stopPrice);

        if (orderResult.IsFailure)
            return orderResult;

        return await orderService.PlaceOrderAsync(orderResult.Value, cancellationToken);
    }
}
