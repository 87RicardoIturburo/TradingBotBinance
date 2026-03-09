using FluentAssertions;
using TradingBot.Core.Entities;
using TradingBot.Core.Enums;
using TradingBot.Core.ValueObjects;

namespace TradingBot.Core.Tests.Entities;

public sealed class TradingStrategyTests
{
    private static TradingStrategy CreateStrategy(string name = "RSI BTC")
    {
        var symbol     = Symbol.Create("BTCUSDT").Value;
        var riskConfig = RiskConfig.Create(100m, 500m, 2m, 4m, 3).Value;

        return TradingStrategy.Create(name, symbol, TradingMode.PaperTrading, riskConfig).Value;
    }

    [Fact]
    public void Create_WithValidParams_ReturnsInactiveStrategy()
    {
        var strategy = CreateStrategy();

        strategy.Status.Should().Be(StrategyStatus.Inactive);
        strategy.IsActive.Should().BeFalse();
        strategy.Name.Should().Be("RSI BTC");
        strategy.Symbol.Value.Should().Be("BTCUSDT");
    }

    [Fact]
    public void Create_WithEmptyName_ReturnsFailure()
    {
        var symbol     = Symbol.Create("BTCUSDT").Value;
        var riskConfig = RiskConfig.Create(100m, 500m, 2m, 4m, 3).Value;

        var result = TradingStrategy.Create("", symbol, TradingMode.PaperTrading, riskConfig);

        result.IsFailure.Should().BeTrue();
    }

    [Fact]
    public void Activate_WithoutRules_ReturnsFailure()
    {
        var strategy = CreateStrategy();

        var result = strategy.Activate();

        result.IsFailure.Should().BeTrue();
    }

    [Fact]
    public void Activate_WithEnabledRule_TransitionsToActive()
    {
        var strategy = CreateStrategy();
        var rule     = CreateRule();
        strategy.AddRule(rule);

        var result = strategy.Activate();

        result.IsSuccess.Should().BeTrue();
        strategy.IsActive.Should().BeTrue();
        strategy.LastActivatedAt.Should().NotBeNull();
        strategy.DomainEvents.Should().ContainSingle();
    }

    [Fact]
    public void Deactivate_FromActive_TransitionsToInactive()
    {
        var strategy = CreateStrategy();
        strategy.AddRule(CreateRule());
        strategy.Activate();

        strategy.Deactivate();

        strategy.Status.Should().Be(StrategyStatus.Inactive);
    }

    [Fact]
    public void UpdateConfig_WhenActive_PublishesHotReloadEvent()
    {
        var strategy = CreateStrategy();
        strategy.AddRule(CreateRule());
        strategy.Activate();
        strategy.ClearDomainEvents();

        var newRisk = RiskConfig.Create(200m, 1000m, 3m, 6m, 5).Value;
        var result  = strategy.UpdateConfig("Updated Name", newRisk, "New desc");

        result.IsSuccess.Should().BeTrue();
        strategy.Name.Should().Be("Updated Name");
        strategy.DomainEvents.Should().ContainSingle();
    }

    [Fact]
    public void AddIndicator_DuplicateType_DoesNotAdd()
    {
        var strategy  = CreateStrategy();
        var indicator = IndicatorConfig.Rsi().Value;
        strategy.AddIndicator(indicator);

        strategy.AddIndicator(indicator);

        strategy.Indicators.Should().HaveCount(1);
    }

    [Fact]
    public void AddRule_Duplicate_ReturnsConflict()
    {
        var strategy = CreateStrategy();
        var rule     = CreateRule();
        strategy.AddRule(rule);

        var result = strategy.AddRule(rule);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("CONFLICT");
    }

    [Fact]
    public void RemoveRule_ExistingRule_RemovesSuccessfully()
    {
        var strategy = CreateStrategy();
        var rule     = CreateRule();
        strategy.AddRule(rule);

        var result = strategy.RemoveRule(rule.Id);

        result.IsSuccess.Should().BeTrue();
        strategy.Rules.Should().BeEmpty();
    }

    private static TradingRule CreateRule()
    {
        var condition = new RuleCondition(
            ConditionOperator.And,
            [new LeafCondition(IndicatorType.RSI, Comparator.LessThan, 30)]);

        var action = new RuleAction(ActionType.BuyMarket, 50m);

        return TradingRule.Create(Guid.NewGuid(), "Buy Low RSI", RuleType.Entry, condition, action).Value;
    }
}
