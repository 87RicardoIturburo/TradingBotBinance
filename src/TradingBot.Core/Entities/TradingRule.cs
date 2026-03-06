using TradingBot.Core.Common;
using TradingBot.Core.Enums;
using TradingBot.Core.ValueObjects;

namespace TradingBot.Core.Entities;

/// <summary>
/// Regla de trading perteneciente a una <see cref="TradingStrategy"/>.
/// Entidad hija del agregado; nunca se accede sin pasar por la estrategia.
/// </summary>
public sealed class TradingRule : Entity<Guid>
{
    public Guid           StrategyId { get; private set; }
    public string         Name       { get; private set; } = string.Empty;
    public RuleType       Type       { get; private set; }
    public bool           IsEnabled  { get; private set; }
    public RuleCondition  Condition  { get; private set; } = null!;
    public RuleAction     Action     { get; private set; } = null!;
    public DateTimeOffset CreatedAt  { get; private set; }
    public DateTimeOffset UpdatedAt  { get; private set; }

    private TradingRule(Guid id) : base(id) { }
    private TradingRule() : base(Guid.Empty) { } // EF Core

    public static Result<TradingRule, DomainError> Create(
        Guid          strategyId,
        string        name,
        RuleType      type,
        RuleCondition condition,
        RuleAction    action)
    {
        if (string.IsNullOrWhiteSpace(name))
            return Result<TradingRule, DomainError>.Failure(
                DomainError.Validation("El nombre de la regla no puede estar vacío."));

        if (name.Length > 100)
            return Result<TradingRule, DomainError>.Failure(
                DomainError.Validation("El nombre de la regla no puede superar 100 caracteres."));

        if (!condition.Conditions.Any())
            return Result<TradingRule, DomainError>.Failure(
                DomainError.Validation("La regla debe tener al menos una condición."));

        if (action.AmountUsdt <= 0)
            return Result<TradingRule, DomainError>.Failure(
                DomainError.Validation("El monto de la acción debe ser mayor que cero."));

        var now = DateTimeOffset.UtcNow;
        return Result<TradingRule, DomainError>.Success(new TradingRule(Guid.NewGuid())
        {
            StrategyId = strategyId,
            Name       = name.Trim(),
            Type       = type,
            IsEnabled  = true,
            Condition  = condition,
            Action     = action,
            CreatedAt  = now,
            UpdatedAt  = now
        });
    }

    public void Enable()
    {
        IsEnabled = true;
        UpdatedAt = DateTimeOffset.UtcNow;
    }

    public void Disable()
    {
        IsEnabled = false;
        UpdatedAt = DateTimeOffset.UtcNow;
    }

    public Result<TradingRule, DomainError> Update(
        string        name,
        RuleCondition condition,
        RuleAction    action)
    {
        if (string.IsNullOrWhiteSpace(name))
            return Result<TradingRule, DomainError>.Failure(
                DomainError.Validation("El nombre de la regla no puede estar vacío."));

        if (!condition.Conditions.Any())
            return Result<TradingRule, DomainError>.Failure(
                DomainError.Validation("La regla debe tener al menos una condición."));

        if (action.AmountUsdt <= 0)
            return Result<TradingRule, DomainError>.Failure(
                DomainError.Validation("El monto de la acción debe ser mayor que cero."));

        Name      = name.Trim();
        Condition = condition;
        Action    = action;
        UpdatedAt = DateTimeOffset.UtcNow;

        return Result<TradingRule, DomainError>.Success(this);
    }
}
