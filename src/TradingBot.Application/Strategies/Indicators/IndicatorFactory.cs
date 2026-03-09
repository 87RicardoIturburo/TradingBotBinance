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
        IndicatorType.RSI => new RsiIndicator((int)config.GetParameter("period", 14)),
        IndicatorType.EMA => new EmaIndicator((int)config.GetParameter("period", 12)),
        IndicatorType.SMA => new SmaIndicator((int)config.GetParameter("period", 20)),
        _                 => throw new NotSupportedException($"Indicador '{config.Type}' no soportado aún.")
    };
}
