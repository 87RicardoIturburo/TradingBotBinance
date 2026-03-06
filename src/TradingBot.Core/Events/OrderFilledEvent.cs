using TradingBot.Core.Enums;
using TradingBot.Core.ValueObjects;

namespace TradingBot.Core.Events;

/// <summary>
/// Publicado cuando una orden es ejecutada total o parcialmente.
/// Dispara la actualización de posición y cálculo de P&amp;L.
/// </summary>
public sealed record OrderFilledEvent(
    Guid OrderId,
    Guid StrategyId,
    Symbol Symbol,
    OrderSide Side,
    Quantity FilledQuantity,
    Price ExecutedPrice,
    bool IsPaperTrade) : DomainEvent;
