namespace TradingBot.Core.Events;

/// <summary>
/// Publicado por el ConfigService cuando una estrategia es modificada
/// en tiempo de ejecución (hot-reload). Todos los motores deben suscribirse
/// para recargar su estado interno.
/// </summary>
public sealed record StrategyUpdatedEvent(
    Guid StrategyId,
    string StrategyName,
    bool IsHotReload) : DomainEvent;
