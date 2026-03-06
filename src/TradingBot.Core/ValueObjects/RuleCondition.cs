using TradingBot.Core.Enums;

namespace TradingBot.Core.ValueObjects;

/// <summary>
/// Árbol de condiciones de una regla. Combina <see cref="LeafCondition"/>
/// con un operador lógico AND / OR / NOT.
/// </summary>
public sealed record RuleCondition(
    ConditionOperator          Operator,
    IReadOnlyList<LeafCondition> Conditions)
{
    public static RuleCondition And(params LeafCondition[] conditions)
        => new(ConditionOperator.And, conditions);

    public static RuleCondition Or(params LeafCondition[] conditions)
        => new(ConditionOperator.Or, conditions);

    public static RuleCondition Not(LeafCondition condition)
        => new(ConditionOperator.Not, [condition]);
}
