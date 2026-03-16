using MediatR;
using TradingBot.Core.Events;

namespace TradingBot.Application.EventHandlers;

/// <summary>
/// Envuelve un <see cref="IDomainEvent"/> como <see cref="INotification"/> de MediatR
/// para poder despachar domain events sin acoplar Core a MediatR.
/// </summary>
public sealed record DomainEventNotification(IDomainEvent DomainEvent) : INotification;
