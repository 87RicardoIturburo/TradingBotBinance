using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using TradingBot.Application.Rules;
using TradingBot.Core.Entities;
using TradingBot.Core.Enums;
using TradingBot.Core.Events;
using TradingBot.Core.ValueObjects;

namespace TradingBot.Application.Tests.Rules;

public sealed class RuleEngineTests
{
    private readonly RuleEngine _sut = new(NullLogger<RuleEngine>.Instance);

    // ── EvaluateAsync ─────────────────────────────────────────────────────

    [Fact]
    public async Task EvaluateAsync_WhenNoEntryRules_ReturnsNull()
    {
        var strategy = CreateStrategy();
        var signal   = CreateSignal(strategy.Id, rsiValue: 25m);

        var result = await _sut.EvaluateAsync(strategy, signal);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().BeNull();
    }

    [Fact]
    public async Task EvaluateAsync_WhenEntryRuleDisabled_ReturnsNull()
    {
        var strategy = CreateStrategy();
        var rule     = CreateEntryRule(strategy.Id, RuleCondition.And(
            new LeafCondition(IndicatorType.RSI, Comparator.LessThan, 30m)));
        strategy.AddRule(rule);
        rule.Disable();

        var signal = CreateSignal(strategy.Id, rsiValue: 25m);

        var result = await _sut.EvaluateAsync(strategy, signal);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().BeNull();
    }

    [Fact]
    public async Task EvaluateAsync_WhenAndConditionAllMet_ReturnsOrder()
    {
        var strategy = CreateStrategy();
        var rule = CreateEntryRule(strategy.Id, RuleCondition.And(
            new LeafCondition(IndicatorType.RSI, Comparator.LessThan, 30m),
            new LeafCondition(IndicatorType.Price, Comparator.GreaterThan, 50000m)));
        strategy.AddRule(rule);

        var signal = CreateSignal(strategy.Id, rsiValue: 25m, price: 55000m);

        var result = await _sut.EvaluateAsync(strategy, signal);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().NotBeNull();
        result.Value!.StrategyId.Should().Be(strategy.Id);
    }

    [Fact]
    public async Task EvaluateAsync_WhenAndConditionPartiallyMet_ReturnsNull()
    {
        var strategy = CreateStrategy();
        var rule = CreateEntryRule(strategy.Id, RuleCondition.And(
            new LeafCondition(IndicatorType.RSI, Comparator.LessThan, 30m),
            new LeafCondition(IndicatorType.Price, Comparator.GreaterThan, 60000m)));
        strategy.AddRule(rule);

        // RSI=25 < 30 ✓, but Price=55000 NOT > 60000 ✗
        var signal = CreateSignal(strategy.Id, rsiValue: 25m, price: 55000m);

        var result = await _sut.EvaluateAsync(strategy, signal);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().BeNull();
    }

    [Fact]
    public async Task EvaluateAsync_WhenOrConditionOneMet_ReturnsOrder()
    {
        var strategy = CreateStrategy();
        var rule = CreateEntryRule(strategy.Id, RuleCondition.Or(
            new LeafCondition(IndicatorType.RSI, Comparator.LessThan, 30m),
            new LeafCondition(IndicatorType.Price, Comparator.GreaterThan, 60000m)));
        strategy.AddRule(rule);

        // RSI=25 < 30 ✓ (one is enough for OR)
        var signal = CreateSignal(strategy.Id, rsiValue: 25m, price: 55000m);

        var result = await _sut.EvaluateAsync(strategy, signal);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().NotBeNull();
    }

    [Fact]
    public async Task EvaluateAsync_WhenOrConditionNoneMet_ReturnsNull()
    {
        var strategy = CreateStrategy();
        var rule = CreateEntryRule(strategy.Id, RuleCondition.Or(
            new LeafCondition(IndicatorType.RSI, Comparator.LessThan, 20m),
            new LeafCondition(IndicatorType.Price, Comparator.GreaterThan, 60000m)));
        strategy.AddRule(rule);

        // RSI=25 NOT < 20, Price=55000 NOT > 60000
        var signal = CreateSignal(strategy.Id, rsiValue: 25m, price: 55000m);

        var result = await _sut.EvaluateAsync(strategy, signal);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().BeNull();
    }

    [Fact]
    public async Task EvaluateAsync_WhenNotConditionNegatesTrue_ReturnsNull()
    {
        var strategy = CreateStrategy();
        // NOT(RSI < 30) → RSI=25 < 30 is true → NOT true → false → no order
        var rule = CreateEntryRule(strategy.Id, RuleCondition.Not(
            new LeafCondition(IndicatorType.RSI, Comparator.LessThan, 30m)));
        strategy.AddRule(rule);

        var signal = CreateSignal(strategy.Id, rsiValue: 25m);

        var result = await _sut.EvaluateAsync(strategy, signal);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().BeNull();
    }

    [Fact]
    public async Task EvaluateAsync_WhenNotConditionNegatesFalse_ReturnsOrder()
    {
        var strategy = CreateStrategy();
        // NOT(RSI < 20) → RSI=25 < 20 is false → NOT false → true → order
        var rule = CreateEntryRule(strategy.Id, RuleCondition.Not(
            new LeafCondition(IndicatorType.RSI, Comparator.LessThan, 20m)));
        strategy.AddRule(rule);

        var signal = CreateSignal(strategy.Id, rsiValue: 25m);

        var result = await _sut.EvaluateAsync(strategy, signal);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().NotBeNull();
    }

    [Fact]
    public async Task EvaluateAsync_WhenFirstRuleFails_EvaluatesSecondRule()
    {
        var strategy = CreateStrategy();

        // First rule: RSI < 20 → won't match (RSI=25)
        var rule1 = CreateEntryRule(strategy.Id, RuleCondition.And(
            new LeafCondition(IndicatorType.RSI, Comparator.LessThan, 20m)));
        strategy.AddRule(rule1);

        // Second rule: RSI < 30 → will match (RSI=25)
        var rule2 = CreateEntryRule(strategy.Id, RuleCondition.And(
            new LeafCondition(IndicatorType.RSI, Comparator.LessThan, 30m)));
        strategy.AddRule(rule2);

        var signal = CreateSignal(strategy.Id, rsiValue: 25m);

        var result = await _sut.EvaluateAsync(strategy, signal);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().NotBeNull();
    }

    [Fact]
    public async Task EvaluateAsync_WhenIndicatorNotInSnapshot_ConditionReturnsFalse()
    {
        var strategy = CreateStrategy();
        // EMA not present in the snapshot
        var rule = CreateEntryRule(strategy.Id, RuleCondition.And(
            new LeafCondition(IndicatorType.EMA, Comparator.GreaterThan, 50000m)));
        strategy.AddRule(rule);

        var signal = CreateSignal(strategy.Id, rsiValue: 25m);

        var result = await _sut.EvaluateAsync(strategy, signal);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().BeNull();
    }

    [Fact]
    public async Task EvaluateAsync_WhenSellMarketAction_ReturnsOrderWithSellSide()
    {
        var strategy = CreateStrategy();
        var action = new RuleAction(ActionType.SellMarket, AmountUsdt: 50m);
        var rule = CreateEntryRule(strategy.Id, RuleCondition.And(
            new LeafCondition(IndicatorType.RSI, Comparator.GreaterThan, 70m)), action);
        strategy.AddRule(rule);

        var signal = CreateSignal(strategy.Id, rsiValue: 75m);

        var result = await _sut.EvaluateAsync(strategy, signal);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().NotBeNull();
        result.Value!.Side.Should().Be(OrderSide.Sell);
    }

    public static TheoryData<Comparator, double, double, bool> ComparatorData => new()
    {
        { Comparator.GreaterThan,        29, 30, false },
        { Comparator.GreaterThan,        31, 30, true  },
        { Comparator.LessThan,           31, 30, false },
        { Comparator.LessThan,           29, 30, true  },
        { Comparator.GreaterThanOrEqual, 30, 30, true  },
        { Comparator.GreaterThanOrEqual, 29, 30, false },
        { Comparator.LessThanOrEqual,    30, 30, true  },
        { Comparator.LessThanOrEqual,    31, 30, false },
        { Comparator.Equal,              30, 30, true  },
        { Comparator.Equal,              29, 30, false },
        { Comparator.NotEqual,           29, 30, true  },
        { Comparator.NotEqual,           30, 30, false },
    };

    [Theory]
    [MemberData(nameof(ComparatorData))]
    public async Task EvaluateAsync_WithComparator_EvaluatesCorrectly(
        Comparator comparator, double rsiValue, double threshold, bool shouldMatch)
    {
        var strategy = CreateStrategy();
        var rule = CreateEntryRule(strategy.Id, RuleCondition.And(
            new LeafCondition(IndicatorType.RSI, comparator, (decimal)threshold)));
        strategy.AddRule(rule);

        var signal = CreateSignal(strategy.Id, rsiValue: (decimal)rsiValue);

        var result = await _sut.EvaluateAsync(strategy, signal);

        result.IsSuccess.Should().BeTrue();
        if (shouldMatch)
            result.Value.Should().NotBeNull();
        else
            result.Value.Should().BeNull();
    }

    // ── EvaluateExitRulesAsync ────────────────────────────────────────────

    [Fact]
    public async Task EvaluateExitRulesAsync_WhenStopLossTriggered_ReturnsExitOrder()
    {
        // Stop-loss at 2%, position PnL at -3%
        var strategy = CreateStrategy(stopLoss: 2m, takeProfit: 4m);
        var position = CreatePosition(strategy.Id, OrderSide.Buy,
            entryPrice: 100m, currentPrice: 97m, quantity: 1m);
        // PnL% = (97-100)/100 * 100 = -3%, which <= -2% (stop-loss)

        var currentPrice = Price.Create(97m).Value;

        var result = await _sut.EvaluateExitRulesAsync(strategy, position, currentPrice);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().NotBeNull();
        result.Value!.Side.Should().Be(OrderSide.Sell);
    }

    [Fact]
    public async Task EvaluateExitRulesAsync_WhenTakeProfitTriggered_ReturnsExitOrder()
    {
        // Take-profit at 4%, position PnL at +5%
        var strategy = CreateStrategy(stopLoss: 2m, takeProfit: 4m);
        var position = CreatePosition(strategy.Id, OrderSide.Buy,
            entryPrice: 100m, currentPrice: 105m, quantity: 1m);
        // PnL% = (105-100)/100 * 100 = 5%, which >= 4% (take-profit)

        var currentPrice = Price.Create(105m).Value;

        var result = await _sut.EvaluateExitRulesAsync(strategy, position, currentPrice);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().NotBeNull();
        result.Value!.Side.Should().Be(OrderSide.Sell);
    }

    [Fact]
    public async Task EvaluateExitRulesAsync_WhenPnLWithinLimits_ReturnsNull()
    {
        // Stop-loss 2%, take-profit 4%, PnL +1% → no exit
        var strategy = CreateStrategy(stopLoss: 2m, takeProfit: 4m);
        var position = CreatePosition(strategy.Id, OrderSide.Buy,
            entryPrice: 100m, currentPrice: 101m, quantity: 1m);

        var currentPrice = Price.Create(101m).Value;

        var result = await _sut.EvaluateExitRulesAsync(strategy, position, currentPrice);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().BeNull();
    }

    [Fact]
    public async Task EvaluateExitRulesAsync_WhenShortPositionStopLoss_ReturnsBuyOrder()
    {
        // Short position: stop-loss triggers when price goes UP
        var strategy = CreateStrategy(stopLoss: 2m, takeProfit: 4m);
        var position = CreatePosition(strategy.Id, OrderSide.Sell,
            entryPrice: 100m, currentPrice: 103m, quantity: 1m);
        // Short PnL% = (100-103)/100 * 100 = -3%, which <= -2% (stop-loss)

        var currentPrice = Price.Create(103m).Value;

        var result = await _sut.EvaluateExitRulesAsync(strategy, position, currentPrice);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().NotBeNull();
        result.Value!.Side.Should().Be(OrderSide.Buy);
    }

    [Fact]
    public async Task EvaluateExitRulesAsync_WhenShortPositionTakeProfit_ReturnsBuyOrder()
    {
        // Short position: take-profit triggers when price goes DOWN
        var strategy = CreateStrategy(stopLoss: 2m, takeProfit: 4m);
        var position = CreatePosition(strategy.Id, OrderSide.Sell,
            entryPrice: 100m, currentPrice: 95m, quantity: 1m);
        // Short PnL% = (100-95)/100 * 100 = 5%, which >= 4% (take-profit)

        var currentPrice = Price.Create(95m).Value;

        var result = await _sut.EvaluateExitRulesAsync(strategy, position, currentPrice);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().NotBeNull();
        result.Value!.Side.Should().Be(OrderSide.Buy);
    }

    [Fact]
    public async Task EvaluateExitRulesAsync_WhenExitRuleConditionMet_ReturnsExitOrder()
    {
        var strategy = CreateStrategy(stopLoss: 2m, takeProfit: 10m);
        var exitRule = CreateExitRule(strategy.Id, RuleCondition.And(
            new LeafCondition(IndicatorType.RSI, Comparator.GreaterThan, 70m)));
        strategy.AddRule(exitRule);

        var position = CreatePosition(strategy.Id, OrderSide.Buy,
            entryPrice: 100m, currentPrice: 101m, quantity: 1m);
        // PnL +1%, within stop/take limits → falls through to exit rules

        var currentPrice = Price.Create(101m).Value;
        // The exit rule evaluation creates a signal with snapshot from current price context.
        // The RSI check requires snapshot parsing — for exit rules the signal is synthetic
        // with snapshot = "ExitEval|PnL=1.00%", so RSI won't be in snapshot → condition false.
        // This tests the case where exit rule condition is NOT met via indicator snapshot.
        // To test exit rule triggering, use a Price-based condition instead.
        strategy.RemoveRule(exitRule.Id);

        var priceExitRule = CreateExitRule(strategy.Id, RuleCondition.And(
            new LeafCondition(IndicatorType.Price, Comparator.GreaterThan, 100m)));
        strategy.AddRule(priceExitRule);

        var result = await _sut.EvaluateExitRulesAsync(strategy, position, currentPrice);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().NotBeNull();
        result.Value!.Side.Should().Be(OrderSide.Sell);
    }

    [Fact]
    public async Task EvaluateExitRulesAsync_WhenExitRuleConditionNotMet_ReturnsNull()
    {
        var strategy = CreateStrategy(stopLoss: 2m, takeProfit: 10m);
        var exitRule = CreateExitRule(strategy.Id, RuleCondition.And(
            new LeafCondition(IndicatorType.Price, Comparator.GreaterThan, 200m)));
        strategy.AddRule(exitRule);

        var position = CreatePosition(strategy.Id, OrderSide.Buy,
            entryPrice: 100m, currentPrice: 101m, quantity: 1m);

        var currentPrice = Price.Create(101m).Value;

        var result = await _sut.EvaluateExitRulesAsync(strategy, position, currentPrice);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().BeNull();
    }

    [Fact]
    public async Task EvaluateExitRulesAsync_WhenDisabledExitRule_SkipsRule()
    {
        var strategy = CreateStrategy(stopLoss: 2m, takeProfit: 10m);
        var exitRule = CreateExitRule(strategy.Id, RuleCondition.And(
            new LeafCondition(IndicatorType.Price, Comparator.GreaterThan, 100m)));
        strategy.AddRule(exitRule);
        exitRule.Disable();

        var position = CreatePosition(strategy.Id, OrderSide.Buy,
            entryPrice: 100m, currentPrice: 101m, quantity: 1m);

        var currentPrice = Price.Create(101m).Value;

        var result = await _sut.EvaluateExitRulesAsync(strategy, position, currentPrice);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().BeNull();
    }

    [Fact]
    public async Task EvaluateAsync_WhenMultipleIndicatorsInSnapshot_ParsesCorrectly()
    {
        var strategy = CreateStrategy();
        // Both RSI < 30 AND EMA > 50000 must be true
        var rule = CreateEntryRule(strategy.Id, RuleCondition.And(
            new LeafCondition(IndicatorType.RSI, Comparator.LessThan, 30m),
            new LeafCondition(IndicatorType.EMA, Comparator.GreaterThan, 50000m)));
        strategy.AddRule(rule);

        var snapshot = $"RSI(14)={28.5m:F4} | EMA(12)={50100m:F4}";
        var signal = CreateSignalWithSnapshot(strategy.Id, price: 55000m, snapshot: snapshot);

        var result = await _sut.EvaluateAsync(strategy, signal);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().NotBeNull();
    }

    [Fact]
    public async Task EvaluateAsync_WhenSnapshotHasEmaBelow_ConditionFails()
    {
        var strategy = CreateStrategy();
        var rule = CreateEntryRule(strategy.Id, RuleCondition.And(
            new LeafCondition(IndicatorType.RSI, Comparator.LessThan, 30m),
            new LeafCondition(IndicatorType.EMA, Comparator.GreaterThan, 50000m)));
        strategy.AddRule(rule);

        var snapshot = $"RSI(14)={28.5m:F4} | EMA(12)={49900m:F4}";
        var signal = CreateSignalWithSnapshot(strategy.Id, price: 55000m, snapshot: snapshot);

        var result = await _sut.EvaluateAsync(strategy, signal);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().BeNull();
    }

    // ── Helpers ────────────────────────────────────────────────────────────

    private static TradingStrategy CreateStrategy(
        decimal stopLoss = 2m,
        decimal takeProfit = 4m)
    {
        var symbol     = Symbol.Create("BTCUSDT").Value;
        var riskConfig = RiskConfig.Create(100m, 500m, stopLoss, takeProfit, 5).Value;
        return TradingStrategy.Create("Test Strategy", symbol, TradingMode.PaperTrading, riskConfig).Value;
    }

    private static TradingRule CreateEntryRule(
        Guid strategyId,
        RuleCondition condition,
        RuleAction? action = null)
    {
        action ??= new RuleAction(ActionType.BuyMarket, AmountUsdt: 50m);
        return TradingRule.Create(strategyId, "Entry Rule", RuleType.Entry, condition, action).Value;
    }

    private static TradingRule CreateExitRule(
        Guid strategyId,
        RuleCondition condition,
        RuleAction? action = null)
    {
        action ??= new RuleAction(ActionType.SellMarket, AmountUsdt: 50m);
        return TradingRule.Create(strategyId, "Exit Rule", RuleType.Exit, condition, action).Value;
    }

    private static SignalGeneratedEvent CreateSignal(
        Guid strategyId,
        decimal rsiValue = 50m,
        decimal price = 55000m)
    {
        var symbol   = Symbol.Create("BTCUSDT").Value;
        var pricevo  = Price.Create(price).Value;
        var snapshot = $"RSI(14)={rsiValue:F4}";
        return new SignalGeneratedEvent(strategyId, symbol, OrderSide.Buy, pricevo, snapshot);
    }

    private static SignalGeneratedEvent CreateSignalWithSnapshot(
        Guid strategyId,
        decimal price,
        string snapshot)
    {
        var symbol  = Symbol.Create("BTCUSDT").Value;
        var pricevo = Price.Create(price).Value;
        return new SignalGeneratedEvent(strategyId, symbol, OrderSide.Buy, pricevo, snapshot);
    }

    private static Position CreatePosition(
        Guid strategyId,
        OrderSide side,
        decimal entryPrice,
        decimal currentPrice,
        decimal quantity)
    {
        var symbol = Symbol.Create("BTCUSDT").Value;
        var entry  = Price.Create(entryPrice).Value;
        var qty    = Quantity.Create(quantity).Value;
        var pos    = Position.Open(strategyId, symbol, side, entry, qty);
        pos.UpdatePrice(Price.Create(currentPrice).Value);
        return pos;
    }
}
