using MediatR;
using TradingBot.Core.Entities;
using TradingBot.Core.Interfaces.Repositories;

namespace TradingBot.Application.Queries.Positions;

/// <summary>Obtiene todas las posiciones abiertas.</summary>
public sealed record GetOpenPositionsQuery
    : IRequest<IReadOnlyList<Position>>;

internal sealed class GetOpenPositionsQueryHandler(
    IPositionRepository repository) : IRequestHandler<GetOpenPositionsQuery, IReadOnlyList<Position>>
{
    public Task<IReadOnlyList<Position>> Handle(
        GetOpenPositionsQuery request,
        CancellationToken cancellationToken)
        => repository.GetOpenPositionsAsync(cancellationToken);
}

/// <summary>Obtiene posiciones cerradas en un rango de fechas.</summary>
public sealed record GetClosedPositionsQuery(
    DateTimeOffset From,
    DateTimeOffset To) : IRequest<IReadOnlyList<Position>>;

internal sealed class GetClosedPositionsQueryHandler(
    IPositionRepository repository) : IRequestHandler<GetClosedPositionsQuery, IReadOnlyList<Position>>
{
    public Task<IReadOnlyList<Position>> Handle(
        GetClosedPositionsQuery request,
        CancellationToken cancellationToken)
        => repository.GetClosedByDateRangeAsync(request.From, request.To, cancellationToken);
}

/// <summary>Resumen de P&amp;L por estrategia para el día actual.</summary>
public sealed record GetPnLSummaryQuery
    : IRequest<IReadOnlyList<PnLSummaryItem>>;

public sealed record PnLSummaryItem(
    Guid    StrategyId,
    string  StrategyName,
    string  Symbol,
    int     OpenPositions,
    decimal UnrealizedPnL,
    decimal DailyRealizedPnL,
    decimal TotalRealizedPnL);

internal sealed class GetPnLSummaryQueryHandler(
    IStrategyRepository strategyRepository,
    IPositionRepository positionRepository) : IRequestHandler<GetPnLSummaryQuery, IReadOnlyList<PnLSummaryItem>>
{
    public async Task<IReadOnlyList<PnLSummaryItem>> Handle(
        GetPnLSummaryQuery request,
        CancellationToken cancellationToken)
    {
        var strategies = await strategyRepository.GetAllAsync(cancellationToken);
        var results = new List<PnLSummaryItem>(strategies.Count);

        foreach (var strategy in strategies)
        {
            var openPositions = await positionRepository
                .GetOpenByStrategyIdAsync(strategy.Id, cancellationToken);

            var dailyPnL = await positionRepository
                .GetDailyRealizedPnLAsync(strategy.Id, cancellationToken);

            var totalRealized = await positionRepository
                .GetTotalRealizedPnLAsync(strategy.Id, cancellationToken);

            results.Add(new PnLSummaryItem(
                strategy.Id,
                strategy.Name,
                strategy.Symbol.Value,
                openPositions.Count,
                openPositions.Sum(p => p.UnrealizedPnL),
                dailyPnL,
                totalRealized));
        }

        return results;
    }
}
