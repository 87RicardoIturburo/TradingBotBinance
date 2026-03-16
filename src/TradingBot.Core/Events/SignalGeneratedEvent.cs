using TradingBot.Core.Enums;
using TradingBot.Core.ValueObjects;

namespace TradingBot.Core.Events;

/// <summary>
/// Publicado por el StrategyEngine cuando los indicadores producen
/// una señal de entrada o salida. El RuleEngine decide si actuar sobre ella.
/// </summary>
public sealed record SignalGeneratedEvent(
    Guid StrategyId,
    Symbol Symbol,
    OrderSide Direction,
    Price CurrentPrice,
    string IndicatorSnapshot,
    decimal? AtrValue = null) : DomainEvent;
