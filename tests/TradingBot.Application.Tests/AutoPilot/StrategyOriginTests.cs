using FluentAssertions;
using TradingBot.Application.AutoPilot;
using TradingBot.Core.Entities;
using TradingBot.Core.Enums;
using TradingBot.Core.ValueObjects;

namespace TradingBot.Application.Tests.AutoPilot;

public sealed class StrategyOriginTests
{
    private static readonly Symbol TestSymbol = Symbol.Create("BTCUSDT").Value;
    private static readonly RiskConfig TestRisk = RiskConfig.Create(100m, 500m, 2m, 4m).Value;

    [Fact]
    public void Create_WithDefaultOrigin_ShouldBeManual()
    {
        var result = TradingStrategy.Create("Test", TestSymbol, TradingMode.PaperTrading, TestRisk);

        result.IsSuccess.Should().BeTrue();
        result.Value.Origin.Should().Be(StrategyOrigin.Manual);
    }

    [Fact]
    public void Create_WithPoolOrigin_ShouldBePool()
    {
        var result = TradingStrategy.Create(
            "Pool-BTCUSDT", TestSymbol, TradingMode.PaperTrading, TestRisk,
            origin: StrategyOrigin.Pool);

        result.IsSuccess.Should().BeTrue();
        result.Value.Origin.Should().Be(StrategyOrigin.Pool);
    }

    [Fact]
    public void Create_WithAutoPilotV1Origin_ShouldBeAutoPilotV1()
    {
        var result = TradingStrategy.Create(
            "AP-BTCUSDT", TestSymbol, TradingMode.PaperTrading, TestRisk,
            origin: StrategyOrigin.AutoPilotV1);

        result.IsSuccess.Should().BeTrue();
        result.Value.Origin.Should().Be(StrategyOrigin.AutoPilotV1);
    }
}

public sealed class DefaultPoolTemplateFactoryTests
{
    private readonly DefaultPoolTemplateFactory _factory = new();

    [Fact]
    public void CreateForSymbol_ShouldProduceStrategyWithPoolOrigin()
    {
        var strategy = _factory.CreateForSymbol("ETHUSDT", TradingMode.PaperTrading, CandleInterval.FifteenMinutes);

        strategy.Origin.Should().Be(StrategyOrigin.Pool);
        strategy.Name.Should().Be("Pool-ETHUSDT");
        strategy.Symbol.Value.Should().Be("ETHUSDT");
    }

    [Fact]
    public void CreateForSymbol_ShouldAlwaysProduceSameIndicators()
    {
        var s1 = _factory.CreateForSymbol("BTCUSDT", TradingMode.PaperTrading, CandleInterval.FifteenMinutes);
        var s2 = _factory.CreateForSymbol("ETHUSDT", TradingMode.PaperTrading, CandleInterval.OneHour);

        s1.Indicators.Select(i => i.Type).Should().BeEquivalentTo(
            s2.Indicators.Select(i => i.Type));
    }

    [Fact]
    public void CreateForSymbol_ShouldContainExpectedIndicators()
    {
        var strategy = _factory.CreateForSymbol("BTCUSDT", TradingMode.PaperTrading, CandleInterval.FifteenMinutes);

        var types = strategy.Indicators.Select(i => i.Type).ToList();
        types.Should().Contain(IndicatorType.RSI);
        types.Should().Contain(IndicatorType.MACD);
        types.Should().Contain(IndicatorType.BollingerBands);
        types.Should().Contain(IndicatorType.ADX);
        types.Should().Contain(IndicatorType.ATR);
        types.Should().Contain(IndicatorType.Volume);
    }

    [Fact]
    public void CreateForSymbol_ShouldHaveEntryAndExitRules()
    {
        var strategy = _factory.CreateForSymbol("BTCUSDT", TradingMode.PaperTrading, CandleInterval.FifteenMinutes);

        strategy.Rules.Count.Should().BeGreaterThanOrEqualTo(2);
        strategy.Rules.Should().Contain(r => r.Type == RuleType.Entry);
        strategy.Rules.Should().Contain(r => r.Type == RuleType.Exit);
    }
}
