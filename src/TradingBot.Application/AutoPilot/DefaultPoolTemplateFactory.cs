using TradingBot.Core.Entities;
using TradingBot.Core.Enums;
using TradingBot.Core.Interfaces.Services;
using TradingBot.Core.ValueObjects;

namespace TradingBot.Application.AutoPilot;

/// <summary>
/// Template por defecto para runners del pool cuando no existe BaseTemplateId en DB.
/// Indicadores estándar y reglas básicas de entrada/salida determinísticos.
/// </summary>
internal sealed class DefaultPoolTemplateFactory : IDefaultPoolTemplateFactory
{
    private static readonly Dictionary<string, decimal> RsiParams = new()
    {
        ["period"] = 14, ["overbought"] = 70, ["oversold"] = 30
    };

    private static readonly Dictionary<string, decimal> MacdParams = new()
    {
        ["fastPeriod"] = 12, ["slowPeriod"] = 26, ["signalPeriod"] = 9
    };

    private static readonly Dictionary<string, decimal> BbParams = new()
    {
        ["period"] = 20, ["stdDev"] = 2
    };

    private static readonly Dictionary<string, decimal> AdxParams = new()
    {
        ["period"] = 14
    };

    private static readonly Dictionary<string, decimal> AtrParams = new()
    {
        ["period"] = 14
    };

    private static readonly Dictionary<string, decimal> VolumeParams = new()
    {
        ["period"] = 20
    };

    public TradingStrategy CreateForSymbol(string symbol, TradingMode mode, CandleInterval timeframe)
    {
        var symbolVo = Symbol.Create(symbol);
        if (symbolVo.IsFailure)
            throw new ArgumentException($"Symbol inválido: {symbol}");

        var riskResult = RiskConfig.Create(
            maxOrderAmountUsdt: 50m,
            maxDailyLossUsdt: 100m,
            stopLossPercent: 2m,
            takeProfitPercent: 3m,
            maxOpenPositions: 3,
            useAtrSizing: true,
            riskPercentPerTrade: 1m,
            atrMultiplier: 2m,
            useTrailingStop: true,
            trailingStopPercent: 1.5m,
            exitOnRegimeChange: true);

        if (riskResult.IsFailure)
            throw new InvalidOperationException($"RiskConfig inválido: {riskResult.Error.Message}");

        var strategyResult = TradingStrategy.Create(
            $"Pool-{symbol}",
            symbolVo.Value,
            mode,
            riskResult.Value,
            "Template por defecto del pool (RSI, MACD, BB, ADX, ATR, Volume)",
            timeframe,
            origin: StrategyOrigin.Pool);

        if (strategyResult.IsFailure)
            throw new InvalidOperationException($"Strategy inválida: {strategyResult.Error.Message}");

        var strategy = strategyResult.Value;

        AddIndicators(strategy);
        AddRules(strategy);

        return strategy;
    }

    private static void AddIndicators(TradingStrategy strategy)
    {
        var indicators = new (IndicatorType Type, Dictionary<string, decimal> Params)[]
        {
            (IndicatorType.RSI, RsiParams),
            (IndicatorType.MACD, MacdParams),
            (IndicatorType.BollingerBands, BbParams),
            (IndicatorType.ADX, AdxParams),
            (IndicatorType.ATR, AtrParams),
            (IndicatorType.Volume, VolumeParams)
        };

        foreach (var (type, parms) in indicators)
        {
            var result = IndicatorConfig.Create(type, new Dictionary<string, decimal>(parms));
            if (result.IsSuccess)
                strategy.AddIndicator(result.Value);
        }
    }

    private static void AddRules(TradingStrategy strategy)
    {
        // Regla de entrada: RSI < 30 AND ADX > 25
        var entryCondition = RuleCondition.And(
            new LeafCondition(IndicatorType.RSI, Comparator.LessThan, 30m),
            new LeafCondition(IndicatorType.ADX, Comparator.GreaterThan, 25m));
        var entryAction = new RuleAction(ActionType.BuyMarket, 50m);
        var entryRule = TradingRule.Create(
            strategy.Id, "Pool Entry — RSI oversold + ADX trending",
            RuleType.Entry, entryCondition, entryAction);
        if (entryRule.IsSuccess)
            strategy.AddRule(entryRule.Value);

        // Regla de salida: RSI > 70
        var exitCondition = RuleCondition.And(
            new LeafCondition(IndicatorType.RSI, Comparator.GreaterThan, 70m));
        var exitAction = new RuleAction(ActionType.SellMarket, 50m);
        var exitRule = TradingRule.Create(
            strategy.Id, "Pool Exit — RSI overbought",
            RuleType.Exit, exitCondition, exitAction);
        if (exitRule.IsSuccess)
            strategy.AddRule(exitRule.Value);
    }
}
