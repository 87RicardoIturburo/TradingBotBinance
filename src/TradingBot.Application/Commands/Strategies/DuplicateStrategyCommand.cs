using MediatR;
using TradingBot.Core.Common;
using TradingBot.Core.Entities;
using TradingBot.Core.Interfaces;
using TradingBot.Core.Interfaces.Repositories;

namespace TradingBot.Application.Commands.Strategies;

/// <summary>Duplica una estrategia existente con todos sus indicadores y reglas.</summary>
public sealed record DuplicateStrategyCommand(
    Guid StrategyId) : IRequest<Result<TradingStrategy, DomainError>>;

internal sealed class DuplicateStrategyCommandHandler(
    IStrategyRepository repository,
    IUnitOfWork unitOfWork) : IRequestHandler<DuplicateStrategyCommand, Result<TradingStrategy, DomainError>>
{
    public async Task<Result<TradingStrategy, DomainError>> Handle(
        DuplicateStrategyCommand request,
        CancellationToken cancellationToken)
    {
        var original = await repository.GetWithRulesAsync(request.StrategyId, cancellationToken);
        if (original is null)
            return Result<TradingStrategy, DomainError>.Failure(
                DomainError.NotFound($"Estrategia '{request.StrategyId}'"));

        var newName = $"{original.Name} (copia)";
        if (newName.Length > 100)
            newName = newName[..100];

        var createResult = TradingStrategy.Create(
            newName, original.Symbol, original.Mode,
            original.RiskConfig, original.Description);

        if (createResult.IsFailure)
            return createResult;

        var copy = createResult.Value;

        // Copiar indicadores
        foreach (var indicator in original.Indicators)
        {
            copy.AddIndicator(indicator);
        }

        // Copiar reglas
        foreach (var rule in original.Rules)
        {
            var ruleResult = TradingRule.Create(
                copy.Id, rule.Name, rule.Type,
                rule.Condition, rule.Action);

            if (ruleResult.IsSuccess)
                copy.AddRule(ruleResult.Value);
        }

        await repository.AddAsync(copy, cancellationToken);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        return Result<TradingStrategy, DomainError>.Success(copy);
    }
}
