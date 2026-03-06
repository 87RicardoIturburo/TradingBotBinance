namespace TradingBot.Core.Events;

/// <summary>
/// Publicado cuando una estrategia cambia su estado activo/inactivo.
/// El frontend recibe este evento vía SignalR para actualizar el dashboard.
/// </summary>
public sealed record StrategyActivatedEvent(
    Guid StrategyId,
    string StrategyName,
    bool IsActive) : DomainEvent;
