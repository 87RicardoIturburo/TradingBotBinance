using MediatR;
using TradingBot.Core.Common;
using TradingBot.Core.Entities;
using TradingBot.Core.Interfaces.Services;

namespace TradingBot.Application.Queries.Orders;

/// <summary>Obtiene todas las órdenes en estado no terminal.</summary>
public sealed record GetOpenOrdersQuery : IRequest<Result<IReadOnlyList<Order>, DomainError>>;

internal sealed class GetOpenOrdersQueryHandler(
    IOrderService orderService) : IRequestHandler<GetOpenOrdersQuery, Result<IReadOnlyList<Order>, DomainError>>
{
    public Task<Result<IReadOnlyList<Order>, DomainError>> Handle(
        GetOpenOrdersQuery request,
        CancellationToken cancellationToken)
        => orderService.GetOpenOrdersAsync(cancellationToken);
}
