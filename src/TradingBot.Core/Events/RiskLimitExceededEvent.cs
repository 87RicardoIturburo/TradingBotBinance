using TradingBot.Core.ValueObjects;

namespace TradingBot.Core.Events;

/// <summary>
/// Publicado por el RiskManager cuando bloquea una orden por superar
/// alguno de los límites configurados. Genera una alerta en el dashboard.
/// </summary>
public sealed record RiskLimitExceededEvent(
    Guid StrategyId,
    Symbol Symbol,
    string LimitType,
    decimal AttemptedAmount,
    decimal AllowedAmount) : DomainEvent;
