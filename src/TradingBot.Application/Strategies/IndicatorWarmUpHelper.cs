using TradingBot.Core.Enums;
using TradingBot.Core.ValueObjects;

namespace TradingBot.Application.Strategies;

/// <summary>
/// Calcula el número de muestras necesarias para que un indicador alcance <c>IsReady</c>.
/// Compartido entre <see cref="StrategyEngine"/> y el Ranker de backtest.
/// </summary>
internal static class IndicatorWarmUpHelper
{
    public static int GetWarmUpPeriod(IndicatorConfig config) => config.Type switch
    {
        IndicatorType.MACD => (int)config.GetParameter("slowPeriod", 26)
                            + (int)config.GetParameter("signalPeriod", 9),
        IndicatorType.ADX  => (int)config.GetParameter("period", 14) * 2,
        _ => (int)config.GetParameter("period", 14)
    };

    public static int GetMaxWarmUpPeriod(IEnumerable<IndicatorConfig> indicators) =>
        indicators
            .Select(GetWarmUpPeriod)
            .DefaultIfEmpty(0)
            .Max();
}
