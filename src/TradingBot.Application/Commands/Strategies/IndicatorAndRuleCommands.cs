using MediatR;
using TradingBot.Core.Common;
using TradingBot.Core.Entities;
using TradingBot.Core.Enums;
using TradingBot.Core.Interfaces;
using TradingBot.Core.Interfaces.Repositories;
using TradingBot.Core.ValueObjects;

namespace TradingBot.Application.Commands.Strategies;

// ── Add Indicator ──────────────────────────────────────────────────────────

/// <summary>Agrega un indicador técnico a una estrategia.</summary>
public sealed record AddIndicatorCommand(
    Guid                       StrategyId,
    IndicatorType              Type,
    Dictionary<string, decimal> Parameters) : IRequest<Result<TradingStrategy, DomainError>>;

internal sealed class AddIndicatorCommandHandler(
    IStrategyRepository repository,
    IUnitOfWork         unitOfWork) : IRequestHandler<AddIndicatorCommand, Result<TradingStrategy, DomainError>>
{
    public async Task<Result<TradingStrategy, DomainError>> Handle(
        AddIndicatorCommand request, CancellationToken cancellationToken)
    {
        var strategy = await repository.GetWithRulesAsync(request.StrategyId, cancellationToken);
        if (strategy is null)
            return Result<TradingStrategy, DomainError>.Failure(
                DomainError.NotFound($"Estrategia '{request.StrategyId}'"));

        var indicatorResult = IndicatorConfig.Create(request.Type, request.Parameters);
        if (indicatorResult.IsFailure)
            return Result<TradingStrategy, DomainError>.Failure(indicatorResult.Error);

        strategy.AddIndicator(indicatorResult.Value);
        await repository.UpdateAsync(strategy, cancellationToken);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        return Result<TradingStrategy, DomainError>.Success(strategy);
    }
}

// ── Remove Indicator ───────────────────────────────────────────────────────

/// <summary>Elimina un indicador técnico de una estrategia.</summary>
public sealed record RemoveIndicatorCommand(
    Guid          StrategyId,
    IndicatorType Type) : IRequest<Result<TradingStrategy, DomainError>>;

internal sealed class RemoveIndicatorCommandHandler(
    IStrategyRepository repository,
    IUnitOfWork         unitOfWork) : IRequestHandler<RemoveIndicatorCommand, Result<TradingStrategy, DomainError>>
{
    public async Task<Result<TradingStrategy, DomainError>> Handle(
        RemoveIndicatorCommand request, CancellationToken cancellationToken)
    {
        var strategy = await repository.GetWithRulesAsync(request.StrategyId, cancellationToken);
        if (strategy is null)
            return Result<TradingStrategy, DomainError>.Failure(
                DomainError.NotFound($"Estrategia '{request.StrategyId}'"));

        strategy.RemoveIndicator(request.Type);
        await repository.UpdateAsync(strategy, cancellationToken);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        return Result<TradingStrategy, DomainError>.Success(strategy);
    }
}

// ── Update Indicator ──────────────────────────────────────────────────────

/// <summary>Actualiza los parámetros de un indicador existente.</summary>
public sealed record UpdateIndicatorCommand(
    Guid                        StrategyId,
    IndicatorType               Type,
    Dictionary<string, decimal> Parameters) : IRequest<Result<TradingStrategy, DomainError>>;

internal sealed class UpdateIndicatorCommandHandler(
    IStrategyRepository repository,
    IUnitOfWork         unitOfWork) : IRequestHandler<UpdateIndicatorCommand, Result<TradingStrategy, DomainError>>
{
    public async Task<Result<TradingStrategy, DomainError>> Handle(
        UpdateIndicatorCommand request, CancellationToken cancellationToken)
    {
        var strategy = await repository.GetWithRulesAsync(request.StrategyId, cancellationToken);
        if (strategy is null)
            return Result<TradingStrategy, DomainError>.Failure(
                DomainError.NotFound($"Estrategia '{request.StrategyId}'"));

        var indicatorResult = IndicatorConfig.Create(request.Type, request.Parameters);
        if (indicatorResult.IsFailure)
            return Result<TradingStrategy, DomainError>.Failure(indicatorResult.Error);

        var updateResult = strategy.UpdateIndicator(indicatorResult.Value);
        if (updateResult.IsFailure)
            return Result<TradingStrategy, DomainError>.Failure(updateResult.Error);

        await repository.UpdateAsync(strategy, cancellationToken);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        return Result<TradingStrategy, DomainError>.Success(strategy);
    }
}

// ── Add Rule ───────────────────────────────────────────────────────────────

/// <summary>Agrega una regla de trading a una estrategia.</summary>
public sealed record AddRuleCommand(
    Guid              StrategyId,
    string            Name,
    RuleType          Type,
    ConditionOperator Operator,
    List<AddRuleCommand.ConditionItem> Conditions,
    ActionType        ActionType,
    decimal           AmountUsdt) : IRequest<Result<TradingStrategy, DomainError>>
{
    public sealed record ConditionItem(
        IndicatorType Indicator,
        Comparator    Comparator,
        decimal       Value);
}

internal sealed class AddRuleCommandHandler(
    IStrategyRepository repository,
    IUnitOfWork         unitOfWork) : IRequestHandler<AddRuleCommand, Result<TradingStrategy, DomainError>>
{
    public async Task<Result<TradingStrategy, DomainError>> Handle(
        AddRuleCommand request, CancellationToken cancellationToken)
    {
        var strategy = await repository.GetWithRulesAsync(request.StrategyId, cancellationToken);
        if (strategy is null)
            return Result<TradingStrategy, DomainError>.Failure(
                DomainError.NotFound($"Estrategia '{request.StrategyId}'"));

        var leafConditions = request.Conditions
            .Select(c => new LeafCondition(c.Indicator, c.Comparator, c.Value))
            .ToList();

        var condition = new RuleCondition(request.Operator, leafConditions);
        var action    = new RuleAction(request.ActionType, request.AmountUsdt);

        var ruleResult = TradingRule.Create(
            request.StrategyId, request.Name, request.Type, condition, action);
        if (ruleResult.IsFailure)
            return Result<TradingStrategy, DomainError>.Failure(ruleResult.Error);

        var addResult = strategy.AddRule(ruleResult.Value);
        if (addResult.IsFailure)
            return Result<TradingStrategy, DomainError>.Failure(addResult.Error);

        await repository.UpdateAsync(strategy, cancellationToken);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        return Result<TradingStrategy, DomainError>.Success(strategy);
    }
}

// ── Remove Rule ────────────────────────────────────────────────────────────

/// <summary>Elimina una regla de trading de una estrategia.</summary>
public sealed record RemoveRuleCommand(
    Guid StrategyId,
    Guid RuleId) : IRequest<Result<TradingStrategy, DomainError>>;

internal sealed class RemoveRuleCommandHandler(
    IStrategyRepository repository,
    IUnitOfWork         unitOfWork) : IRequestHandler<RemoveRuleCommand, Result<TradingStrategy, DomainError>>
{
    public async Task<Result<TradingStrategy, DomainError>> Handle(
        RemoveRuleCommand request, CancellationToken cancellationToken)
    {
        var strategy = await repository.GetWithRulesAsync(request.StrategyId, cancellationToken);
        if (strategy is null)
            return Result<TradingStrategy, DomainError>.Failure(
                DomainError.NotFound($"Estrategia '{request.StrategyId}'"));

        var result = strategy.RemoveRule(request.RuleId);
        if (result.IsFailure)
            return Result<TradingStrategy, DomainError>.Failure(result.Error);

        await repository.UpdateAsync(strategy, cancellationToken);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        return Result<TradingStrategy, DomainError>.Success(strategy);
    }
}

// ── Update Rule ───────────────────────────────────────────────────────────

/// <summary>Actualiza una regla de trading existente.</summary>
public sealed record UpdateRuleCommand(
    Guid              StrategyId,
    Guid              RuleId,
    string            Name,
    ConditionOperator Operator,
    List<UpdateRuleCommand.ConditionItem> Conditions,
    ActionType        ActionType,
    decimal           AmountUsdt) : IRequest<Result<TradingStrategy, DomainError>>
{
    public sealed record ConditionItem(
        IndicatorType Indicator,
        Comparator    Comparator,
        decimal       Value);
}

internal sealed class UpdateRuleCommandHandler(
    IStrategyRepository repository,
    IUnitOfWork         unitOfWork) : IRequestHandler<UpdateRuleCommand, Result<TradingStrategy, DomainError>>
{
    public async Task<Result<TradingStrategy, DomainError>> Handle(
        UpdateRuleCommand request, CancellationToken cancellationToken)
    {
        var strategy = await repository.GetWithRulesAsync(request.StrategyId, cancellationToken);
        if (strategy is null)
            return Result<TradingStrategy, DomainError>.Failure(
                DomainError.NotFound($"Estrategia '{request.StrategyId}'"));

        var ruleResult = strategy.GetRule(request.RuleId);
        if (ruleResult.IsFailure)
            return Result<TradingStrategy, DomainError>.Failure(ruleResult.Error);

        var leafConditions = request.Conditions
            .Select(c => new LeafCondition(c.Indicator, c.Comparator, c.Value))
            .ToList();

        var condition = new RuleCondition(request.Operator, leafConditions);
        var action    = new RuleAction(request.ActionType, request.AmountUsdt);

        var updateResult = ruleResult.Value.Update(request.Name, condition, action);
        if (updateResult.IsFailure)
            return Result<TradingStrategy, DomainError>.Failure(updateResult.Error);

        await repository.UpdateAsync(strategy, cancellationToken);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        return Result<TradingStrategy, DomainError>.Success(strategy);
    }
}
