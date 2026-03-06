using TradingBot.Core.Enums;

namespace TradingBot.Core.ValueObjects;

/// <summary>
/// Acción a ejecutar cuando se cumple la condición de una regla.
/// <para><paramref name="LimitPriceOffsetPercent"/>: desplazamiento opcional
/// sobre el precio de mercado para órdenes Limit (positivo = por encima,
/// negativo = por debajo).</para>
/// </summary>
public sealed record RuleAction(
    ActionType Type,
    decimal    AmountUsdt,
    decimal?   LimitPriceOffsetPercent = null);
