namespace TradingBot.Core.Enums;

public enum Comparator
{
    GreaterThan,
    LessThan,
    GreaterThanOrEqual,
    LessThanOrEqual,
    Equal,
    NotEqual,
    CrossAbove,   // Cruce alcista (p. ej. precio cruza EMA hacia arriba)
    CrossBelow    // Cruce bajista
}