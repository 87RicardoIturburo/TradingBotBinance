using MediatR;
using TradingBot.Core.Common;
using TradingBot.Core.Entities;
using TradingBot.Core.Interfaces.Services;

namespace TradingBot.Application.Commands.Orders;

/// <summary>Cancela una orden activa.</summary>
public sealed record CancelOrderCommand(Guid OrderId) : IRequest<Result<Order, DomainError>>;

internal sealed class CancelOrderCommandHandler(
    IOrderService orderService) : IRequestHandler<CancelOrderCommand, Result<Order, DomainError>>
{
    public Task<Result<Order, DomainError>> Handle(
        CancelOrderCommand request,
        CancellationToken cancellationToken)
        => orderService.CancelOrderAsync(request.OrderId, cancellationToken);
}
