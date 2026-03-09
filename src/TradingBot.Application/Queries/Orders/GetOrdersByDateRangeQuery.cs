using MediatR;
using TradingBot.Core.Entities;
using TradingBot.Core.Interfaces.Repositories;

namespace TradingBot.Application.Queries.Orders;

/// <summary>Obtiene el historial de órdenes en un rango de fechas.</summary>
public sealed record GetOrdersByDateRangeQuery(
    DateTimeOffset From,
    DateTimeOffset To) : IRequest<IReadOnlyList<Order>>;

internal sealed class GetOrdersByDateRangeQueryHandler(
    IOrderRepository orderRepository) : IRequestHandler<GetOrdersByDateRangeQuery, IReadOnlyList<Order>>
{
    public Task<IReadOnlyList<Order>> Handle(
        GetOrdersByDateRangeQuery request,
        CancellationToken cancellationToken)
        => orderRepository.GetByDateRangeAsync(request.From, request.To, cancellationToken);
}
