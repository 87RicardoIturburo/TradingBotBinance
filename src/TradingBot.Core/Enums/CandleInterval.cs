namespace TradingBot.Core.Enums;

/// <summary>
/// Intervalos de velas soportados. Mapean directamente a los intervalos de Binance Kline WebSocket.
/// </summary>
public enum CandleInterval
{
    OneMinute,
    FiveMinutes,
    FifteenMinutes,
    ThirtyMinutes,
    OneHour,
    FourHours,
    OneDay
}
