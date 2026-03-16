using TradingBot.Core.Enums;
using TradingBot.Core.Interfaces.Trading;
using TradingBot.Core.ValueObjects;

namespace TradingBot.Application.Strategies.Indicators;

/// <summary>
/// Crea instancias de <see cref="ITechnicalIndicator"/> a partir de <see cref="IndicatorConfig"/>.
/// </summary>
internal static class IndicatorFactory
{
    public static ITechnicalIndicator Create(IndicatorConfig config) => config.Type switch
    {
        IndicatorType.RSI              => new RsiIndicator((int)config.GetParameter("period", 14)),
        IndicatorType.EMA              => new EmaIndicator((int)config.GetParameter("period", 12)),
        IndicatorType.SMA              => new SmaIndicator((int)config.GetParameter("period", 20)),
        IndicatorType.MACD             => new MacdIndicator(
                                              (int)config.GetParameter("fastPeriod", 12),
                                              (int)config.GetParameter("slowPeriod", 26),
                                              (int)config.GetParameter("signalPeriod", 9)),
        IndicatorType.BollingerBands   => new BollingerBandsIndicator(
                                              (int)config.GetParameter("period", 20),
                                              config.GetParameter("stdDev", 2m)),
        IndicatorType.Fibonacci        => new FibonacciIndicator(
                                              (int)config.GetParameter("period", 50)),
        IndicatorType.LinearRegression => new LinearRegressionIndicator(
                                              (int)config.GetParameter("period", 20)),
        IndicatorType.ADX              => new AdxIndicator(
                                              (int)config.GetParameter("period", 14)),
        IndicatorType.ATR              => new AtrIndicator(
                                              (int)config.GetParameter("period", 14)),
        _                              => throw new NotSupportedException($"Indicador '{config.Type}' no soportado aún.")
    };
}
