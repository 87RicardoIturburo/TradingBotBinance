namespace TradingBot.Core.Enums;

/// <summary>
/// Extensiones para <see cref="CandleInterval"/>.
/// Centraliza la conversión a minutos usada por StrategyEngine, BacktestEngine y warm-up.
/// </summary>
public static class CandleIntervalExtensions
{
    /// <summary>Duración en minutos del intervalo de vela.</summary>
    public static int ToMinutes(this CandleInterval interval) => interval switch
    {
        CandleInterval.OneMinute      => 1,
        CandleInterval.FiveMinutes    => 5,
        CandleInterval.FifteenMinutes => 15,
        CandleInterval.ThirtyMinutes  => 30,
        CandleInterval.OneHour        => 60,
        CandleInterval.FourHours      => 240,
        CandleInterval.OneDay         => 1440,
        _                             => 1
    };
}
