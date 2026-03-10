using MediatR;
using TradingBot.Core.Common;
using TradingBot.Core.Entities;
using TradingBot.Core.Interfaces.Services;
using TradingBot.Core.ValueObjects;

namespace TradingBot.Application.Commands.Strategies;

/// <summary>Guarda los rangos de optimización de una estrategia para reutilización futura.</summary>
public sealed record SaveOptimizationProfileCommand(
    Guid                                StrategyId,
    IReadOnlyList<SavedParameterRange>  Ranges) : IRequest<Result<TradingStrategy, DomainError>>;

internal sealed class SaveOptimizationProfileCommandHandler(
    IStrategyConfigService configService) : IRequestHandler<SaveOptimizationProfileCommand, Result<TradingStrategy, DomainError>>
{
    public async Task<Result<TradingStrategy, DomainError>> Handle(
        SaveOptimizationProfileCommand request,
        CancellationToken cancellationToken)
    {
        var getResult = await configService.GetByIdAsync(request.StrategyId, cancellationToken);
        if (getResult.IsFailure)
            return getResult;

        var strategy = getResult.Value;
        strategy.UpdateOptimizationRanges(request.Ranges);

        return await configService.UpdateAsync(strategy, cancellationToken);
    }
}
