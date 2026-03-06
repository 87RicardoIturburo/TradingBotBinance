namespace TradingBot.Core.Events;

/// <summary>
/// Contrato base para todos los eventos de dominio.
/// Los eventos son inmutables y representan algo que ya ocurrió.
/// </summary>
public interface IDomainEvent
{
    /// <summary>Identificador único del evento.</summary>
    Guid EventId { get; }

    /// <summary>Momento exacto (UTC) en que ocurrió el evento.</summary>
    DateTimeOffset OccurredAt { get; }
}
