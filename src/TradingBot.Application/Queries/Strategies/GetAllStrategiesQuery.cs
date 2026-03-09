using MediatR;
using TradingBot.Core.Entities;
using TradingBot.Core.Interfaces.Services;

namespace TradingBot.Application.Queries.Strategies;

/// <summary>Obtiene todas las estrategias.</summary>
public sealed record GetAllStrategiesQuery : IRequest<IReadOnlyList<TradingStrategy>>;

internal sealed class GetAllStrategiesQueryHandler(
    IStrategyConfigService configService) : IRequestHandler<GetAllStrategiesQuery, IReadOnlyList<TradingStrategy>>
{
    public Task<IReadOnlyList<TradingStrategy>> Handle(
        GetAllStrategiesQuery request,
        CancellationToken cancellationToken)
        => configService.GetAllAsync(cancellationToken);
}
