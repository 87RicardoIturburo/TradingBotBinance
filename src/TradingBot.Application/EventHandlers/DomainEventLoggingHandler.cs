using MediatR;
using Microsoft.Extensions.Logging;
using TradingBot.Core.Events;

namespace TradingBot.Application.EventHandlers;

/// <summary>
/// Handler genérico que loguea todos los domain events despachados.
/// Handlers específicos pueden crearse para eventos individuales.
/// </summary>
internal sealed class DomainEventLoggingHandler(ILogger<DomainEventLoggingHandler> logger)
    : INotificationHandler<DomainEventNotification>
{
    public Task Handle(DomainEventNotification notification, CancellationToken cancellationToken)
    {
        var domainEvent = notification.DomainEvent;

        switch (domainEvent)
        {
            case OrderPlacedEvent e:
                logger.LogInformation(
                    "📋 Evento: Orden colocada {OrderId} — {Side} {Qty} {Symbol} (paper={Paper})",
                    e.OrderId, e.Side, e.Quantity.Value, e.Symbol.Value, e.IsPaperTrade);
                break;

            case OrderFilledEvent e:
                logger.LogInformation(
                    "✅ Evento: Orden ejecutada {OrderId} — {Side} {Qty} {Symbol} @ {Price}",
                    e.OrderId, e.Side, e.FilledQuantity.Value, e.Symbol.Value, e.ExecutedPrice.Value);
                break;

            case OrderCancelledEvent e:
                logger.LogInformation(
                    "❌ Evento: Orden cancelada {OrderId} — {Reason}",
                    e.OrderId, e.Reason);
                break;

            case StrategyActivatedEvent e:
                logger.LogInformation(
                    "🔄 Evento: Estrategia '{Name}' ({Id}) — Activa={Active}",
                    e.StrategyName, e.StrategyId, e.IsActive);
                break;

            case RiskLimitExceededEvent e:
                logger.LogWarning(
                    "⚠ Evento: Límite de riesgo excedido — {Type} en {Symbol}: intentó {Attempted}, permitido {Allowed}",
                    e.LimitType, e.Symbol.Value, e.AttemptedAmount, e.AllowedAmount);
                break;

            default:
                logger.LogDebug(
                    "📌 Evento de dominio: {Type} ({Id})",
                    domainEvent.GetType().Name, domainEvent.EventId);
                break;
        }

        return Task.CompletedTask;
    }
}
