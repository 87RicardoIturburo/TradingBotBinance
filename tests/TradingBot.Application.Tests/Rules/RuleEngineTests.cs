using System.Globalization;
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

        var snapshot = string.Create(CultureInfo.InvariantCulture,
            $"RSI(14)={28.5m:F4} | EMA(12)={50100m:F4}");
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

    // ── ATR dynamic stop-loss tests ──────────────────────────────────────

    [Fact]
    public async Task EvaluateExitRulesAsync_WhenAtrStopLossTriggered_ReturnsExitOrder()
    {
        // ATR = 500, multiplier = 2 → stop distance = 1000
        // Long entry at 50000, SL price = 49000. Current price = 48900 → triggered
        var strategy = CreateAtrStrategy(atrMultiplier: 2m);
        var position = CreatePosition(strategy.Id, OrderSide.Buy,
            entryPrice: 50000m, currentPrice: 48900m, quantity: 0.01m);

        var currentPrice = Price.Create(48900m).Value;

        var result = await _sut.EvaluateExitRulesAsync(
            strategy, position, currentPrice, atrValue: 500m);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().NotBeNull();
        result.Value!.Side.Should().Be(OrderSide.Sell);
    }

    [Fact]
    public async Task EvaluateExitRulesAsync_WhenAtrStopLossNotTriggered_ReturnsNull()
    {
        // ATR = 500, multiplier = 2 → stop distance = 1000
        // Long entry at 50000, SL price = 49000. Current price = 49500 → NOT triggered
        var strategy = CreateAtrStrategy(atrMultiplier: 2m);
        var position = CreatePosition(strategy.Id, OrderSide.Buy,
            entryPrice: 50000m, currentPrice: 49500m, quantity: 0.01m);

        var currentPrice = Price.Create(49500m).Value;

        var result = await _sut.EvaluateExitRulesAsync(
            strategy, position, currentPrice, atrValue: 500m);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().BeNull();
    }

    [Fact]
    public async Task EvaluateExitRulesAsync_WhenAtrShortStopLossTriggered_ReturnsBuyOrder()
    {
        // Short entry at 50000, ATR = 500, multiplier = 2 → SL price = 51000
        // Current price = 51100 → triggered
        var strategy = CreateAtrStrategy(atrMultiplier: 2m);
        var position = CreatePosition(strategy.Id, OrderSide.Sell,
            entryPrice: 50000m, currentPrice: 51100m, quantity: 0.01m);

        var currentPrice = Price.Create(51100m).Value;

        var result = await _sut.EvaluateExitRulesAsync(
            strategy, position, currentPrice, atrValue: 500m);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().NotBeNull();
        result.Value!.Side.Should().Be(OrderSide.Buy);
    }

    [Fact]
    public async Task EvaluateExitRulesAsync_WhenAtrEnabledButNoAtrValue_FallsBackToPercentage()
    {
        // UseAtrSizing = true but ATR = null → falls back to percentage-based stop-loss
        // Stop-loss at 2%, PnL = -3% → should trigger
        var strategy = CreateAtrStrategy(atrMultiplier: 2m);
        var position = CreatePosition(strategy.Id, OrderSide.Buy,
            entryPrice: 100m, currentPrice: 97m, quantity: 1m);

        var currentPrice = Price.Create(97m).Value;

        var result = await _sut.EvaluateExitRulesAsync(
            strategy, position, currentPrice, atrValue: null);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().NotBeNull();
        result.Value!.Side.Should().Be(OrderSide.Sell);
    }

    [Fact]
    public async Task EvaluateExitRulesAsync_WhenAtrTakeProfitStillPercentage_TriggersCorrectly()
    {
        // UseAtrSizing affects stop-loss only; take-profit is always percentage-based
        // Take-profit at 4%, PnL = +5% → should trigger regardless of ATR
        var strategy = CreateAtrStrategy(atrMultiplier: 2m);
        var position = CreatePosition(strategy.Id, OrderSide.Buy,
            entryPrice: 100m, currentPrice: 105m, quantity: 1m);

        var currentPrice = Price.Create(105m).Value;

        var result = await _sut.EvaluateExitRulesAsync(
            strategy, position, currentPrice, atrValue: 2m);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().NotBeNull();
        result.Value!.Side.Should().Be(OrderSide.Sell);
    }

    // ── P2-1: Epsilon tolerance for Equal/NotEqual ─────────────────────────

    [Theory]
    [InlineData(30.00001, 30, true)]
    [InlineData(29.99999, 30, true)]
    [InlineData(30.001, 30, false)]
    [InlineData(29.999, 30, false)]
    public async Task EvaluateAsync_WhenEqualWithEpsilon_MatchesWithinTolerance(
        decimal rsiValue, decimal threshold, bool shouldMatch)
    {
        var strategy = CreateStrategy();
        var rule = CreateEntryRule(strategy.Id, RuleCondition.And(
            new LeafCondition(IndicatorType.RSI, Comparator.Equal, threshold)));
        strategy.AddRule(rule);

        var signal = CreateSignal(strategy.Id, rsiValue: rsiValue);

        var result = await _sut.EvaluateAsync(strategy, signal);

        result.IsSuccess.Should().BeTrue();
        if (shouldMatch)
            result.Value.Should().NotBeNull();
        else
            result.Value.Should().BeNull();
    }

    // ── P2-3: Trailing Stop-Loss ─────────────────────────────────────────

    [Fact]
    public async Task EvaluateExitRulesAsync_TrailingStop_TriggersOnPullbackFromPeak()
    {
        // Long entry at 100, price went to 120 (peak), now pulled back to 117.
        // TrailingStop = 1.5% from peak → trigger at 120 * 0.985 = 118.2.
        // Current = 117 < 118.2 → should trigger.
        var strategy = CreateTrailingStopStrategy(trailingStopPercent: 1.5m);
        var position = CreatePosition(strategy.Id, OrderSide.Buy,
            entryPrice: 100m, currentPrice: 117m, quantity: 1m);

        // Simulate that price reached 120 before pulling back
        position.UpdatePrice(Price.Create(120m).Value);
        position.UpdatePrice(Price.Create(117m).Value);

        var currentPrice = Price.Create(117m).Value;

        var result = await _sut.EvaluateExitRulesAsync(
            strategy, position, currentPrice);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().NotBeNull("trailing stop should trigger on pullback from peak");
        result.Value!.Side.Should().Be(OrderSide.Sell);
    }

    [Fact]
    public async Task EvaluateExitRulesAsync_TrailingStop_DoesNotTriggerWhileRising()
    {
        // Long entry at 100, peak = 110, current = 109.
        // TrailingStop = 1.5% → trigger at 110 * 0.985 = 108.35.
        // Current = 109 > 108.35 → should NOT trigger.
        var strategy = CreateTrailingStopStrategy(trailingStopPercent: 1.5m);
        var position = CreatePosition(strategy.Id, OrderSide.Buy,
            entryPrice: 100m, currentPrice: 109m, quantity: 1m);

        position.UpdatePrice(Price.Create(110m).Value);
        position.UpdatePrice(Price.Create(109m).Value);

        var currentPrice = Price.Create(109m).Value;

        var result = await _sut.EvaluateExitRulesAsync(
            strategy, position, currentPrice);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().BeNull("price is still above trailing stop level");
    }

    [Fact]
    public async Task EvaluateExitRulesAsync_TrailingStop_DoesNotActivateInLoss()
    {
        // Long entry at 100, price never went above 100 → no profit.
        // TrailingStop should NOT activate when position is in loss.
        var strategy = CreateTrailingStopStrategy(trailingStopPercent: 1.5m);
        var position = CreatePosition(strategy.Id, OrderSide.Buy,
            entryPrice: 100m, currentPrice: 98m, quantity: 1m);

        var currentPrice = Price.Create(98m).Value;

        var result = await _sut.EvaluateExitRulesAsync(
            strategy, position, currentPrice);

        // Should fall through to stop-loss instead, not trailing stop
        result.IsSuccess.Should().BeTrue();
        // With 2% SL default, 2% drop should trigger regular SL
        result.Value.Should().NotBeNull();
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

    private static TradingStrategy CreateAtrStrategy(
        decimal atrMultiplier = 2m,
        decimal stopLoss = 2m,
        decimal takeProfit = 4m)
    {
        var symbol     = Symbol.Create("BTCUSDT").Value;
        var riskConfig = RiskConfig.Create(
            100m, 500m, stopLoss, takeProfit, 5,
            useAtrSizing: true, riskPercentPerTrade: 1m, atrMultiplier: atrMultiplier).Value;
        return TradingStrategy.Create("ATR Test Strategy", symbol, TradingMode.PaperTrading, riskConfig).Value;
    }

    private static TradingStrategy CreateTrailingStopStrategy(
        decimal trailingStopPercent = 1.5m,
        decimal stopLoss = 2m,
        decimal takeProfit = 10m)
    {
        var symbol     = Symbol.Create("BTCUSDT").Value;
        var riskConfig = RiskConfig.Create(
            100m, 500m, stopLoss, takeProfit, 5,
            useTrailingStop: true, trailingStopPercent: trailingStopPercent).Value;
        return TradingStrategy.Create("Trailing Test Strategy", symbol, TradingMode.PaperTrading, riskConfig).Value;
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
        var snapshot = string.Create(CultureInfo.InvariantCulture, $"RSI(14)={rsiValue:F4}");
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

    // ── Tests de exit rule con indicatorSnapshot ─────────────────────────

    [Fact]
    public async Task EvaluateExitRulesAsync_WhenIndicatorSnapshotProvided_RsiExitRuleFires()
    {
        // BUGFIX: antes de la corrección, el snapshot era "ExitEval|PnL=..." y
        // el RSI no se podía parsear → el exit rule nunca se activaba.
        // Con el snapshot real, RSI(14)=72 > 65 → debe activarse.
        var strategy = CreateStrategy(stopLoss: 2m, takeProfit: 10m);
        var exitRule = CreateExitRule(strategy.Id, RuleCondition.And(
            new LeafCondition(IndicatorType.RSI, Comparator.GreaterThan, 65m)));
        strategy.AddRule(exitRule);

        var position = CreatePosition(strategy.Id, OrderSide.Buy,
            entryPrice: 100m, currentPrice: 103m, quantity: 1m); // PnL +3%, dentro de límites

        var currentPrice = Price.Create(103m).Value;
        var realSnapshot = "RSI(13)=72.0000 | BollingerBands(17,2)=101.5000"; // RSI > 65

        var result = await _sut.EvaluateExitRulesAsync(
            strategy, position, currentPrice,
            indicatorSnapshot: realSnapshot);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().NotBeNull("el exit rule RSI > 65 debe activarse con snapshot real");
        result.Value!.Side.Should().Be(OrderSide.Sell);
    }

    [Fact]
    public async Task EvaluateExitRulesAsync_WhenNoSnapshotProvided_RsiExitRuleDoesNotFire()
    {
        // Sin snapshot real, la condición RSI no puede evaluarse → no dispara.
        var strategy = CreateStrategy(stopLoss: 2m, takeProfit: 10m);
        var exitRule = CreateExitRule(strategy.Id, RuleCondition.And(
            new LeafCondition(IndicatorType.RSI, Comparator.GreaterThan, 65m)));
        strategy.AddRule(exitRule);

        var position = CreatePosition(strategy.Id, OrderSide.Buy,
            entryPrice: 100m, currentPrice: 103m, quantity: 1m);

        var currentPrice = Price.Create(103m).Value;

        // Sin pasar indicatorSnapshot
        var result = await _sut.EvaluateExitRulesAsync(
            strategy, position, currentPrice);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().BeNull("sin snapshot de indicadores, RSI no puede parsearse");
    }

    [Fact]
    public async Task EvaluateExitRulesAsync_WhenSnapshotRsiBelowThreshold_ExitRuleDoesNotFire()
    {
        // RSI actual = 55, threshold = 65 → condición no cumplida, no dispara
        var strategy = CreateStrategy(stopLoss: 2m, takeProfit: 10m);
        var exitRule = CreateExitRule(strategy.Id, RuleCondition.And(
            new LeafCondition(IndicatorType.RSI, Comparator.GreaterThan, 65m)));
        strategy.AddRule(exitRule);

        var position = CreatePosition(strategy.Id, OrderSide.Buy,
            entryPrice: 100m, currentPrice: 103m, quantity: 1m);

        var currentPrice = Price.Create(103m).Value;
        var snapshot = "RSI(13)=55.0000"; // RSI = 55, NOT > 65

        var result = await _sut.EvaluateExitRulesAsync(
            strategy, position, currentPrice,
            indicatorSnapshot: snapshot);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().BeNull("RSI=55 no supera el umbral de salida de 65");
    }

    // ── EST-4: Scaled Take-Profit ────────────────────────────────────────

    [Fact]
    public async Task EvaluateExitRulesAsync_WhenScaledTp1Reached_ReturnsPartialCloseOrder()
    {
        var strategy = CreateScaledTpStrategy(tp1Percent: 2m, tp1ClosePercent: 50m, tp2Percent: 5m);
        var position = CreatePosition(strategy.Id, OrderSide.Buy,
            entryPrice: 100m, currentPrice: 102.5m, quantity: 1m);

        var currentPrice = Price.Create(102.5m).Value;

        var result = await _sut.EvaluateExitRulesAsync(strategy, position, currentPrice);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().NotBeNull();
        result.Value!.Quantity.Value.Should().BeApproximately(0.5m, 0.01m);
    }

    [Fact]
    public async Task EvaluateExitRulesAsync_WhenScaledTp2Reached_ReturnsLargerPartialClose()
    {
        var strategy = CreateScaledTpStrategy(tp1Percent: 2m, tp1ClosePercent: 50m, tp2Percent: 5m, tp2ClosePercent: 60m);
        var position = CreatePosition(strategy.Id, OrderSide.Buy,
            entryPrice: 100m, currentPrice: 106m, quantity: 1m);

        var currentPrice = Price.Create(106m).Value;

        var result = await _sut.EvaluateExitRulesAsync(strategy, position, currentPrice);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().NotBeNull();
        result.Value!.Quantity.Value.Should().BeApproximately(0.6m, 0.01m);
    }

    [Fact]
    public async Task EvaluateExitRulesAsync_WhenScaledTpNotReached_FallsToSimpleTp()
    {
        var strategy = CreateScaledTpStrategy(tp1Percent: 2m, tp1ClosePercent: 50m, simpleTp: 10m);
        var position = CreatePosition(strategy.Id, OrderSide.Buy,
            entryPrice: 100m, currentPrice: 101m, quantity: 1m);

        var currentPrice = Price.Create(101m).Value;

        var result = await _sut.EvaluateExitRulesAsync(strategy, position, currentPrice);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().BeNull();
    }

    private static TradingStrategy CreateScaledTpStrategy(
        decimal tp1Percent = 2m,
        decimal tp1ClosePercent = 50m,
        decimal tp2Percent = 0m,
        decimal tp2ClosePercent = 60m,
        decimal simpleTp = 10m)
    {
        var symbol     = Symbol.Create("BTCUSDT").Value;
        var riskConfig = RiskConfig.Create(
            100m, 500m, 2m, simpleTp, 5,
            takeProfit1Percent: tp1Percent,
            takeProfit1ClosePercent: tp1ClosePercent,
            takeProfit2Percent: tp2Percent,
            takeProfit2ClosePercent: tp2ClosePercent).Value;
        return TradingStrategy.Create("Scaled TP Strategy", symbol, TradingMode.PaperTrading, riskConfig).Value;
    }

    // ── Regime Change Exit ────────────────────────────────────────────────

    [Fact]
    public async Task EvaluateExitRulesAsync_WhenRegimeChangeEnabled_AndAdxDrops_ClosesPosition()
    {
        var strategy = CreateRegimeChangeStrategy();
        var position = CreatePosition(strategy.Id, OrderSide.Buy,
            entryPrice: 100m, currentPrice: 101m, quantity: 1m);

        var currentPrice = Price.Create(101m).Value;
        var snapshot = "ADX(14)=15.0000 | RSI(14)=55.0000";

        var result = await _sut.EvaluateExitRulesAsync(
            strategy, position, currentPrice,
            indicatorSnapshot: snapshot);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().NotBeNull();
        result.Value!.Side.Should().Be(OrderSide.Sell);
    }

    [Fact]
    public async Task EvaluateExitRulesAsync_WhenRegimeChangeDisabled_AndAdxDrops_DoesNotClose()
    {
        var strategy = CreateStrategy(stopLoss: 2m, takeProfit: 10m);
        var position = CreatePosition(strategy.Id, OrderSide.Buy,
            entryPrice: 100m, currentPrice: 101m, quantity: 1m);

        var currentPrice = Price.Create(101m).Value;
        var snapshot = "ADX(14)=15.0000 | RSI(14)=55.0000";

        var result = await _sut.EvaluateExitRulesAsync(
            strategy, position, currentPrice,
            indicatorSnapshot: snapshot);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().BeNull();
    }

    // ── Time-Based Exit ──────────────────────────────────────────────────

    [Fact]
    public async Task EvaluateExitRulesAsync_WhenPositionExceedsMaxDuration_ClosesPosition()
    {
        var strategy = CreateTimeLimitedStrategy(maxCandles: 6);
        var position = CreateOldPosition(strategy.Id, OrderSide.Buy,
            entryPrice: 100m, currentPrice: 100.5m, quantity: 1m,
            openedMinutesAgo: 10);

        var currentPrice = Price.Create(100.5m).Value;

        var result = await _sut.EvaluateExitRulesAsync(strategy, position, currentPrice);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().NotBeNull();
    }

    [Fact]
    public async Task EvaluateExitRulesAsync_WhenPositionWithinDuration_DoesNotClose()
    {
        var strategy = CreateTimeLimitedStrategy(maxCandles: 60);
        var position = CreateOldPosition(strategy.Id, OrderSide.Buy,
            entryPrice: 100m, currentPrice: 100.5m, quantity: 1m,
            openedMinutesAgo: 5);

        var currentPrice = Price.Create(100.5m).Value;

        var result = await _sut.EvaluateExitRulesAsync(strategy, position, currentPrice);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().BeNull();
    }

    private static TradingStrategy CreateRegimeChangeStrategy()
    {
        var symbol     = Symbol.Create("BTCUSDT").Value;
        var riskConfig = RiskConfig.Create(
            100m, 500m, 2m, 10m, 5,
            exitOnRegimeChange: true).Value;
        return TradingStrategy.Create("Regime Change Strategy", symbol, TradingMode.PaperTrading, riskConfig).Value;
    }

    private static TradingStrategy CreateTimeLimitedStrategy(int maxCandles = 6)
    {
        var symbol     = Symbol.Create("BTCUSDT").Value;
        var riskConfig = RiskConfig.Create(
            100m, 500m, 2m, 10m, 5,
            maxPositionDurationCandles: maxCandles).Value;
        return TradingStrategy.Create("Time Limited Strategy", symbol, TradingMode.PaperTrading, riskConfig).Value;
    }

    private static Position CreateOldPosition(
        Guid strategyId,
        OrderSide side,
        decimal entryPrice,
        decimal currentPrice,
        decimal quantity,
        int openedMinutesAgo)
    {
        var symbol = Symbol.Create("BTCUSDT").Value;
        var entry  = Price.Create(entryPrice).Value;
        var qty    = Quantity.Create(quantity).Value;
        var pos    = Position.Open(strategyId, symbol, side, entry, qty);
        pos.UpdatePrice(Price.Create(currentPrice).Value);
        // Position.OpenedAt se establece en Open() con DateTimeOffset.UtcNow.
        // Para simular una posición antigua, usamos reflection (solo en tests).
        var openedAtField = typeof(Position).GetProperty("OpenedAt")!;
        openedAtField.SetValue(pos, DateTimeOffset.UtcNow.AddMinutes(-openedMinutesAgo));
        return pos;
    }
}
