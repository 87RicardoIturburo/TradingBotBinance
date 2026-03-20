using MediatR;
using TradingBot.Core.Entities;
using TradingBot.Core.Interfaces.Repositories;

namespace TradingBot.Application.Explainer;

/// <summary>
/// Obtiene las órdenes ejecutadas (con explicación) de una estrategia
/// en un rango de fechas, ordenadas por fecha descendente.
/// </summary>
public sealed record GetTradeHistoryQuery(
    Guid? StrategyId,
    DateTimeOffset From,
    DateTimeOffset To,
    int Limit = 50) : IRequest<IReadOnlyList<Order>>;

internal sealed class GetTradeHistoryQueryHandler(
    IOrderRepository orderRepository) : IRequestHandler<GetTradeHistoryQuery, IReadOnlyList<Order>>
{
    public async Task<IReadOnlyList<Order>> Handle(
        GetTradeHistoryQuery request,
        CancellationToken cancellationToken)
    {
        var orders = await orderRepository.GetByDateRangeAsync(
            request.From, request.To, cancellationToken);

        IEnumerable<Order> filtered = orders
            .Where(o => o.Status == Core.Enums.OrderStatus.Filled);

        if (request.StrategyId.HasValue)
            filtered = filtered.Where(o => o.StrategyId == request.StrategyId.Value);

        return filtered
            .OrderByDescending(o => o.FilledAt ?? o.CreatedAt)
            .Take(request.Limit)
            .ToList();
    }
}
