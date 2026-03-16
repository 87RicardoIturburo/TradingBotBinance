using MediatR;
using TradingBot.Core.Common;
using TradingBot.Core.Entities;
using TradingBot.Core.Enums;
using TradingBot.Core.Interfaces.Services;
using TradingBot.Core.ValueObjects;

namespace TradingBot.Application.Commands.Strategies;

/// <summary>Crea una nueva estrategia en estado Inactive.</summary>
public sealed record CreateStrategyCommand(
    string      Name,
    string      SymbolValue,
    TradingMode Mode,
    decimal     MaxOrderAmountUsdt,
    decimal     MaxDailyLossUsdt,
    decimal     StopLossPercent,
    decimal     TakeProfitPercent,
    int         MaxOpenPositions,
    string?     Description = null,
    bool        UseAtrSizing = false,
    decimal     RiskPercentPerTrade = 1m,
    decimal     AtrMultiplier = 2m) : IRequest<Result<TradingStrategy, DomainError>>;

internal sealed class CreateStrategyCommandHandler(
    IStrategyConfigService configService) : IRequestHandler<CreateStrategyCommand, Result<TradingStrategy, DomainError>>
{
    public async Task<Result<TradingStrategy, DomainError>> Handle(
        CreateStrategyCommand request,
        CancellationToken cancellationToken)
    {
        var symbolResult = Symbol.Create(request.SymbolValue);
        if (symbolResult.IsFailure)
            return Result<TradingStrategy, DomainError>.Failure(symbolResult.Error);

        var riskResult = RiskConfig.Create(
            request.MaxOrderAmountUsdt,
            request.MaxDailyLossUsdt,
            request.StopLossPercent,
            request.TakeProfitPercent,
            request.MaxOpenPositions,
            request.UseAtrSizing,
            request.RiskPercentPerTrade,
            request.AtrMultiplier);

        if (riskResult.IsFailure)
            return Result<TradingStrategy, DomainError>.Failure(riskResult.Error);

        var strategyResult = TradingStrategy.Create(
            request.Name, symbolResult.Value, request.Mode,
            riskResult.Value, request.Description);

        if (strategyResult.IsFailure)
            return strategyResult;

        return await configService.CreateAsync(strategyResult.Value, cancellationToken);
    }
}
