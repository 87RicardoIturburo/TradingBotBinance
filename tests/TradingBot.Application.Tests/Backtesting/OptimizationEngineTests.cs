using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using TradingBot.Application.Backtesting;
using TradingBot.Core.Common;
using TradingBot.Core.Entities;
using TradingBot.Core.Enums;
using TradingBot.Core.Events;
using TradingBot.Core.Interfaces.Services;
using TradingBot.Core.Interfaces.Trading;
using TradingBot.Core.ValueObjects;

namespace TradingBot.Application.Tests.Backtesting;

public sealed class OptimizationEngineTests
{
    private readonly BacktestEngine _backtestEngine = new(NullLogger<BacktestEngine>.Instance);
    private readonly OptimizationEngine _optimizer;

    public OptimizationEngineTests()
    {
        _optimizer = new OptimizationEngine(
            _backtestEngine,
            NullLogger<OptimizationEngine>.Instance);
    }

    private static TradingStrategy CreateStrategy()
    {
        var symbol = Symbol.Create("BTCUSDT").Value;
        var risk = RiskConfig.Create(100, 500, 5, 5, 5).Value;
        var strategy = TradingStrategy.Create("Test", symbol, TradingMode.PaperTrading, risk).Value;

        var rsiConfig = IndicatorConfig.Create(IndicatorType.RSI, new Dictionary<string, decimal>
        {
            ["period"] = 14, ["overbought"] = 70, ["oversold"] = 30
        }).Value;
        strategy.AddIndicator(rsiConfig);

        var rule = TradingRule.Create(strategy.Id, "Buy RSI", RuleType.Entry,
            new RuleCondition(ConditionOperator.And,
                [new LeafCondition(IndicatorType.RSI, Comparator.LessThan, 30)]),
            new RuleAction(ActionType.BuyMarket, 50)).Value;
        strategy.AddRule(rule);

        return strategy;
    }

    private static List<Kline> GenerateKlines(int count, decimal startPrice = 50000m)
    {
        var klines = new List<Kline>();
        var baseTime = new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero);
        for (var i = 0; i < count; i++)
        {
            var price = startPrice + (i % 20 - 10) * 100m;
            klines.Add(new Kline(baseTime.AddMinutes(i), price, price + 50, price - 50, price, 100m));
        }
        return klines;
    }

    [Fact]
    public void GenerateCombinations_WhenSingleRange_ReturnsCorrectValues()
    {
        var ranges = new List<ParameterRange>
        {
            new("stopLossPercent", 2, 4, 1)
        };

        var combos = OptimizationEngine.GenerateCombinations(ranges);

        combos.Should().HaveCount(3);
        combos[0]["stopLossPercent"].Should().Be(2);
        combos[1]["stopLossPercent"].Should().Be(3);
        combos[2]["stopLossPercent"].Should().Be(4);
    }

    [Fact]
    public void GenerateCombinations_WhenTwoRanges_ReturnsCartesianProduct()
    {
        var ranges = new List<ParameterRange>
        {
            new("stopLossPercent", 2, 3, 1),   // 2 values
            new("takeProfitPercent", 4, 6, 1)   // 3 values
        };

        var combos = OptimizationEngine.GenerateCombinations(ranges);

        combos.Should().HaveCount(6); // 2 × 3
        combos.Should().Contain(c => c["stopLossPercent"] == 2 && c["takeProfitPercent"] == 4);
        combos.Should().Contain(c => c["stopLossPercent"] == 3 && c["takeProfitPercent"] == 6);
    }

    [Fact]
    public void GenerateCombinations_WhenEmpty_ReturnsSingleEmptyDict()
    {
        var combos = OptimizationEngine.GenerateCombinations([]);

        combos.Should().HaveCount(1);
        combos[0].Should().BeEmpty();
    }

    [Fact]
    public async Task RunAsync_WhenNoSignals_AllCombinationsHaveZeroTrades()
    {
        var strategy = CreateStrategy();
        var klines = GenerateKlines(100);
        var ranges = new List<ParameterRange>
        {
            new("stopLossPercent", 2, 3, 1)
        };

        var mockStrategy = Substitute.For<ITradingStrategy>();
        var mockRuleEngine = Substitute.For<IRuleEngine>();

        mockStrategy.ProcessTickAsync(Arg.Any<MarketTickReceivedEvent>(), Arg.Any<CancellationToken>())
            .Returns(Result<SignalGeneratedEvent?, DomainError>.Success(null));

        var result = await _optimizer.RunAsync(
            strategy, ranges, klines,
            (_, _) => Task.FromResult((mockStrategy, mockRuleEngine)),
            CancellationToken.None);

        result.TotalCombinations.Should().Be(2);
        result.CompletedCombinations.Should().Be(2);
        result.Results.Should().HaveCount(2);
        result.Results.Should().AllSatisfy(r => r.TotalTrades.Should().Be(0));
    }

    [Fact]
    public async Task RunAsync_ResultsAreRankedByPnLDescending()
    {
        var strategy = CreateStrategy();
        var klines = GenerateKlines(100);
        var ranges = new List<ParameterRange>
        {
            new("stopLossPercent", 2, 4, 1)
        };

        var mockStrategy = Substitute.For<ITradingStrategy>();
        var mockRuleEngine = Substitute.For<IRuleEngine>();

        mockStrategy.ProcessTickAsync(Arg.Any<MarketTickReceivedEvent>(), Arg.Any<CancellationToken>())
            .Returns(Result<SignalGeneratedEvent?, DomainError>.Success(null));

        var result = await _optimizer.RunAsync(
            strategy, ranges, klines,
            (_, _) => Task.FromResult((mockStrategy, mockRuleEngine)),
            CancellationToken.None);

        result.Results.Should().BeInDescendingOrder(r => r.TotalPnL);
        result.Results[0].Rank.Should().Be(1);
        result.Results[^1].Rank.Should().Be(result.Results.Count);
    }

    [Fact]
    public async Task RunAsync_WhenCancelled_ThrowsOperationCancelled()
    {
        var strategy = CreateStrategy();
        var klines = GenerateKlines(1000);
        var ranges = new List<ParameterRange>
        {
            new("stopLossPercent", 1, 10, 1)
        };

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var mockStrategy = Substitute.For<ITradingStrategy>();
        var mockRuleEngine = Substitute.For<IRuleEngine>();

        var act = () => _optimizer.RunAsync(
            strategy, ranges, klines,
            (_, _) => Task.FromResult((mockStrategy, mockRuleEngine)),
            cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task RunAsync_ParametersArePropagatedToResults()
    {
        var strategy = CreateStrategy();
        var klines = GenerateKlines(50);
        var ranges = new List<ParameterRange>
        {
            new("stopLossPercent", 3, 3, 1),
            new("RSI.period", 14, 14, 1)
        };

        var mockStrategy = Substitute.For<ITradingStrategy>();
        var mockRuleEngine = Substitute.For<IRuleEngine>();

        mockStrategy.ProcessTickAsync(Arg.Any<MarketTickReceivedEvent>(), Arg.Any<CancellationToken>())
            .Returns(Result<SignalGeneratedEvent?, DomainError>.Success(null));

        var result = await _optimizer.RunAsync(
            strategy, ranges, klines,
            (_, _) => Task.FromResult((mockStrategy, mockRuleEngine)),
            CancellationToken.None);

        result.Results.Should().HaveCount(1);
        result.Results[0].Parameters.Should().ContainKey("stopLossPercent").WhoseValue.Should().Be(3);
        result.Results[0].Parameters.Should().ContainKey("RSI.period").WhoseValue.Should().Be(14);
    }
}
