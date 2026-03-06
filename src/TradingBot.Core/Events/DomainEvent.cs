namespace TradingBot.Core.Events;

/// <summary>
/// Implementación base de <see cref="IDomainEvent"/> usando record para inmutabilidad.
/// Todos los eventos de dominio deben heredar de esta clase.
/// </summary>
public abstract record DomainEvent : IDomainEvent
{
    public Guid EventId { get; } = Guid.NewGuid();
    public DateTimeOffset OccurredAt { get; } = DateTimeOffset.UtcNow;
}
