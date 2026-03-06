namespace TradingBot.Core.Events;

/// <summary>
/// Publicado cuando una orden es cancelada, ya sea por el usuario,
/// por expiración o por el RiskManager.
/// </summary>
public sealed record OrderCancelledEvent(
    Guid OrderId,
    Guid StrategyId,
    string Reason) : DomainEvent;
