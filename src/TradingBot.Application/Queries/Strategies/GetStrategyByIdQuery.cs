using MediatR;
using TradingBot.Core.Common;
using TradingBot.Core.Entities;
using TradingBot.Core.Interfaces.Services;

namespace TradingBot.Application.Queries.Strategies;

/// <summary>Obtiene una estrategia por Id incluyendo sus reglas.</summary>
public sealed record GetStrategyByIdQuery(Guid Id) : IRequest<Result<TradingStrategy, DomainError>>;

internal sealed class GetStrategyByIdQueryHandler(
    IStrategyConfigService configService) : IRequestHandler<GetStrategyByIdQuery, Result<TradingStrategy, DomainError>>
{
    public Task<Result<TradingStrategy, DomainError>> Handle(
        GetStrategyByIdQuery request,
        CancellationToken cancellationToken)
        => configService.GetByIdAsync(request.Id, cancellationToken);
}
