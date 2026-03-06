using TradingBot.Core.Enums;
using TradingBot.Core.ValueObjects;

namespace TradingBot.Core.Events;

/// <summary>
/// Publicado por el OrderManager cuando una orden es enviada al exchange
/// (o simulada en modo Paper Trading).
/// </summary>
public sealed record OrderPlacedEvent(
    Guid OrderId,
    Guid StrategyId,
    Symbol Symbol,
    OrderSide Side,
    OrderType Type,
    Quantity Quantity,
    Price? LimitPrice,
    bool IsPaperTrade) : DomainEvent;
