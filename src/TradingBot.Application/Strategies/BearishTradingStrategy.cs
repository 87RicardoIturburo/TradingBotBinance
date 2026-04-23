using Microsoft.Extensions.Logging;
using TradingBot.Application.Strategies.Indicators;
using TradingBot.Core.Enums;

namespace TradingBot.Application.Strategies;

/// <summary>
/// Estrategia para mercados bajistas confirmados.
/// En Spot solo genera señales Sell (cierre de posiciones Long).
/// Usa pullback bajista a EMA21 como señal principal.
/// </summary>
internal sealed class BearishTradingStrategy : DefaultTradingStrategy
{
    public BearishTradingStrategy(ILogger<DefaultTradingStrategy> logger) : base(logger) { }

    private protected override (OrderSide? Side, IndicatorType Source, SignalNature Nature) DetermineSignalCandidate(decimal price)
    {
        var result = TryBearishEma21Pullback(price);
        if (result.Side is not null) return result;

        result = TryMacdSignal();
        if (result.Side is not null)
        {
            if (result.Side == OrderSide.Buy)
                return (null, default, default);
            return result;
        }

        result = TryRsiSignal();
        if (result.Side is not null)
        {
            if (result.Side == OrderSide.Buy)
                return (null, default, default);
            return result;
        }

        return (null, default, default);
    }

    private (OrderSide? Side, IndicatorType Source, SignalNature Nature) TryBearishEma21Pullback(decimal price)
    {
        if (!_indicators.TryGetValue(IndicatorType.EMA, out var emaInd) || !emaInd.IsReady)
            return (null, default, default);

        var emaValue = emaInd.Calculate()!.Value;
        if (emaValue == 0) return (null, default, default);

        var distancePercent = Math.Abs(price - emaValue) / emaValue;
        if (distancePercent > 0.005m)
            return (null, default, default);

        if (!_indicators.TryGetValue(IndicatorType.RSI, out var rsiInd) || !rsiInd.IsReady)
            return (null, default, default);

        var rsi = rsiInd.Calculate()!.Value;
        if (rsi < 40m || rsi > 60m)
            return (null, default, default);

        if (price <= emaValue)
            return (OrderSide.Sell, IndicatorType.EMA, SignalNature.TrendFollowing);

        return (null, default, default);
    }
}
