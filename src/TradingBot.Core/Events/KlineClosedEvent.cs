using TradingBot.Core.Enums;
using TradingBot.Core.ValueObjects;

namespace TradingBot.Core.Events;

/// <summary>
/// Emitido cuando una vela (kline) se cierra en el WebSocket de Binance.
/// Los indicadores técnicos deben actualizarse SOLO con este evento.
/// </summary>
public sealed record KlineClosedEvent(
    Symbol Symbol,
    CandleInterval Interval,
    decimal Open,
    decimal High,
    decimal Low,
    decimal Close,
    decimal Volume,
    DateTimeOffset OpenTime,
    DateTimeOffset CloseTime) : DomainEvent;
