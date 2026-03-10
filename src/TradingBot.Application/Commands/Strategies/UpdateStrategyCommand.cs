using MediatR;
using TradingBot.Core.Common;
using TradingBot.Core.Entities;
using TradingBot.Core.Enums;
using TradingBot.Core.Interfaces.Services;
using TradingBot.Core.ValueObjects;

namespace TradingBot.Application.Commands.Strategies;

/// <summary>Actualiza la configuración de una estrategia existente (hot-reload si activa).</summary>
public sealed record UpdateStrategyCommand(
    Guid         Id,
    string       Name,
    string?      Symbol,
    TradingMode? Mode,
    decimal      MaxOrderAmountUsdt,
    decimal      MaxDailyLossUsdt,
    decimal      StopLossPercent,
    decimal      TakeProfitPercent,
    int          MaxOpenPositions,
    string?      Description = null) : IRequest<Result<TradingStrategy, DomainError>>;

internal sealed class UpdateStrategyCommandHandler(
    IStrategyConfigService configService) : IRequestHandler<UpdateStrategyCommand, Result<TradingStrategy, DomainError>>
{
    public async Task<Result<TradingStrategy, DomainError>> Handle(
        UpdateStrategyCommand request,
        CancellationToken cancellationToken)
    {
        var getResult = await configService.GetByIdAsync(request.Id, cancellationToken);
        if (getResult.IsFailure)
            return getResult;

        var strategy = getResult.Value;

        // Actualizar símbolo si se proporcionó y es distinto
        if (!string.IsNullOrWhiteSpace(request.Symbol)
            && !request.Symbol.Equals(strategy.Symbol.Value, StringComparison.OrdinalIgnoreCase))
        {
            var symbolResult = Symbol.Create(request.Symbol);
            if (symbolResult.IsFailure)
                return Result<TradingStrategy, DomainError>.Failure(symbolResult.Error);

            var updateSymbol = strategy.UpdateSymbol(symbolResult.Value);
            if (updateSymbol.IsFailure)
                return updateSymbol;
        }

        // Actualizar modo si se proporcionó y es distinto
        if (request.Mode.HasValue && request.Mode.Value != strategy.Mode)
        {
            var updateMode = strategy.UpdateMode(request.Mode.Value);
            if (updateMode.IsFailure)
                return updateMode;
        }

        var riskResult = RiskConfig.Create(
            request.MaxOrderAmountUsdt,
            request.MaxDailyLossUsdt,
            request.StopLossPercent,
            request.TakeProfitPercent,
            request.MaxOpenPositions);

        if (riskResult.IsFailure)
            return Result<TradingStrategy, DomainError>.Failure(riskResult.Error);

        var updateResult = strategy.UpdateConfig(
            request.Name, riskResult.Value, request.Description);

        if (updateResult.IsFailure)
            return updateResult;

        return await configService.UpdateAsync(strategy, cancellationToken);
    }
}
