using TradingBot.Core.ValueObjects;

namespace TradingBot.Core.Events;

/// <summary>
/// Publicado por el MarketEngine cada vez que llega un tick de precio
/// desde el WebSocket de Binance. Es el evento de mayor frecuencia del sistema.
/// </summary>
public sealed record MarketTickReceivedEvent(
    Symbol Symbol,
    Price BidPrice,
    Price AskPrice,
    Price LastPrice,
    decimal Volume,
    DateTimeOffset Timestamp) : DomainEvent;
