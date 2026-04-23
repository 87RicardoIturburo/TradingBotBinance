using Microsoft.Extensions.Logging;
using TradingBot.Application.Strategies.Indicators;
using TradingBot.Core.Enums;

namespace TradingBot.Application.Strategies;

/// <summary>
/// Estrategia optimizada para mercados en tendencia.
/// Prioriza MACD y pullbacks a EMA21 sobre indicadores de reversión a la media.
/// Solo opera en la dirección de la tendencia (ADX DI+/DI-).
/// </summary>
internal sealed class TrendingTradingStrategy : DefaultTradingStrategy
{
    public TrendingTradingStrategy(ILogger<DefaultTradingStrategy> logger) : base(logger) { }

    private protected override (OrderSide? Side, IndicatorType Source, SignalNature Nature) DetermineSignalCandidate(decimal price)
    {
        var result = TryMacdSignal();
        if (result.Side is not null) return result;

        result = TryEma21Pullback(price);
        if (result.Side is not null) return result;

        result = TryEmaSignal(price);
        if (result.Side is not null) return result;

        result = TrySmaSignal(price);
        if (result.Side is not null) return result;

        return (null, default, default);
    }

    private (OrderSide? Side, IndicatorType Source, SignalNature Nature) TryEma21Pullback(decimal price)
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

        var adx = GetAdxIndicator();

        if (adx is { IsReady: true, IsBullish: true } && price >= emaValue)
            return (OrderSide.Buy, IndicatorType.EMA, SignalNature.TrendFollowing);

        if (adx is { IsReady: true, IsBearish: true } && price <= emaValue)
            return (OrderSide.Sell, IndicatorType.EMA, SignalNature.TrendFollowing);

        return (null, default, default);
    }
}
