using Microsoft.Extensions.Logging;
using TradingBot.Application.Strategies.Indicators;
using TradingBot.Core.Enums;

namespace TradingBot.Application.Strategies;

/// <summary>
/// Estrategia optimizada para mercados laterales (rango).
/// Prioriza RSI y Bollinger Bands como soporte/resistencia.
/// No usa Fibonacci — opera reversión a la media con BB upper/lower.
/// </summary>
internal sealed class RangingTradingStrategy : DefaultTradingStrategy
{
    public RangingTradingStrategy(ILogger<DefaultTradingStrategy> logger) : base(logger) { }

    private protected override (OrderSide? Side, IndicatorType Source, SignalNature Nature) DetermineSignalCandidate(decimal price)
    {
        var result = TryRsiSignal();
        if (result.Side is not null) return result;

        result = TryBollingerSupportResistance(price);
        if (result.Side is not null) return result;

        result = TryMacdSignal();
        if (result.Side is not null) return result;

        return (null, default, default);
    }

    private (OrderSide? Side, IndicatorType Source, SignalNature Nature) TryBollingerSupportResistance(decimal price)
    {
        if (!_indicators.TryGetValue(IndicatorType.BollingerBands, out var bbRaw)
            || bbRaw is not BollingerBandsIndicator { IsReady: true } bb)
            return (null, default, default);

        if (!_indicators.TryGetValue(IndicatorType.RSI, out var rsiInd) || !rsiInd.IsReady)
            return (null, default, default);

        var rsi = rsiInd.Calculate()!.Value;

        if (price <= bb.LowerBand!.Value && rsi < 35m)
            return (OrderSide.Buy, IndicatorType.BollingerBands, SignalNature.MeanReversion);

        if (price >= bb.UpperBand!.Value && rsi > 65m)
            return (OrderSide.Sell, IndicatorType.BollingerBands, SignalNature.MeanReversion);

        return (null, default, default);
    }
}
