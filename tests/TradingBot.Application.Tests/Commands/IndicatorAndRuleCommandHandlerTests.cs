using FluentAssertions;
using NSubstitute;
using TradingBot.Application.Commands.Strategies;
using TradingBot.Core.Entities;
using TradingBot.Core.Enums;
using TradingBot.Core.Interfaces;
using TradingBot.Core.Interfaces.Repositories;
using TradingBot.Core.ValueObjects;

namespace TradingBot.Application.Tests.Commands;

public sealed class AddIndicatorCommandHandlerTests
{
    private readonly IStrategyRepository _strategyRepo = Substitute.For<IStrategyRepository>();
    private readonly IUnitOfWork         _unitOfWork   = Substitute.For<IUnitOfWork>();
    private readonly AddIndicatorCommandHandler _sut;

    public AddIndicatorCommandHandlerTests()
    {
        _sut = new AddIndicatorCommandHandler(_strategyRepo, _unitOfWork);
    }

    [Fact]
    public async Task Handle_WhenStrategyNotFound_ReturnsNotFoundError()
    {
        var command = new AddIndicatorCommand(Guid.NewGuid(), IndicatorType.RSI,
            new Dictionary<string, decimal> { ["period"] = 14, ["overbought"] = 70, ["oversold"] = 30 });

        _strategyRepo.GetWithRulesAsync(command.StrategyId, Arg.Any<CancellationToken>())
            .Returns((TradingStrategy?)null);

        var result = await _sut.Handle(command, CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("NOT_FOUND");
    }

    [Fact]
    public async Task Handle_WhenValidIndicator_AddsAndSaves()
    {
        var strategy = CreateStrategy();
        var command = new AddIndicatorCommand(strategy.Id, IndicatorType.RSI,
            new Dictionary<string, decimal> { ["period"] = 14, ["overbought"] = 70, ["oversold"] = 30 });

        _strategyRepo.GetWithRulesAsync(strategy.Id, Arg.Any<CancellationToken>())
            .Returns(strategy);

        var result = await _sut.Handle(command, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Indicators.Should().ContainSingle(i => i.Type == IndicatorType.RSI);
        await _strategyRepo.Received(1).UpdateAsync(strategy, Arg.Any<CancellationToken>());
        await _unitOfWork.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_WhenInvalidParameters_ReturnsValidationError()
    {
        var strategy = CreateStrategy();
        // RSI with period < 2 is invalid
        var command = new AddIndicatorCommand(strategy.Id, IndicatorType.RSI,
            new Dictionary<string, decimal> { ["period"] = 1 });

        _strategyRepo.GetWithRulesAsync(strategy.Id, Arg.Any<CancellationToken>())
            .Returns(strategy);

        var result = await _sut.Handle(command, CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("VALIDATION_ERROR");
    }

    private static TradingStrategy CreateStrategy()
    {
        var symbol     = Symbol.Create("BTCUSDT").Value;
        var riskConfig = RiskConfig.Create(100m, 500m, 2m, 4m, 5).Value;
        return TradingStrategy.Create("Test Strategy", symbol, TradingMode.PaperTrading, riskConfig).Value;
    }
}

public sealed class RemoveIndicatorCommandHandlerTests
{
    private readonly IStrategyRepository _strategyRepo = Substitute.For<IStrategyRepository>();
    private readonly IUnitOfWork         _unitOfWork   = Substitute.For<IUnitOfWork>();
    private readonly RemoveIndicatorCommandHandler _sut;

    public RemoveIndicatorCommandHandlerTests()
    {
        _sut = new RemoveIndicatorCommandHandler(_strategyRepo, _unitOfWork);
    }

    [Fact]
    public async Task Handle_WhenStrategyNotFound_ReturnsNotFoundError()
    {
        var command = new RemoveIndicatorCommand(Guid.NewGuid(), IndicatorType.RSI);

        _strategyRepo.GetWithRulesAsync(command.StrategyId, Arg.Any<CancellationToken>())
            .Returns((TradingStrategy?)null);

        var result = await _sut.Handle(command, CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("NOT_FOUND");
    }

    [Fact]
    public async Task Handle_WhenIndicatorExists_RemovesAndSaves()
    {
        var strategy = CreateStrategyWithRsi();
        strategy.Indicators.Should().ContainSingle(i => i.Type == IndicatorType.RSI);

        var command = new RemoveIndicatorCommand(strategy.Id, IndicatorType.RSI);

        _strategyRepo.GetWithRulesAsync(strategy.Id, Arg.Any<CancellationToken>())
            .Returns(strategy);

        var result = await _sut.Handle(command, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Indicators.Should().NotContain(i => i.Type == IndicatorType.RSI);
        await _unitOfWork.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_WhenIndicatorDoesNotExist_StillSucceeds()
    {
        var strategy = CreateStrategyWithRsi();
        var command = new RemoveIndicatorCommand(strategy.Id, IndicatorType.EMA);

        _strategyRepo.GetWithRulesAsync(strategy.Id, Arg.Any<CancellationToken>())
            .Returns(strategy);

        var result = await _sut.Handle(command, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        // RSI should still be there
        result.Value.Indicators.Should().ContainSingle(i => i.Type == IndicatorType.RSI);
    }

    private static TradingStrategy CreateStrategyWithRsi()
    {
        var symbol     = Symbol.Create("BTCUSDT").Value;
        var riskConfig = RiskConfig.Create(100m, 500m, 2m, 4m, 5).Value;
        var strategy   = TradingStrategy.Create("Test Strategy", symbol, TradingMode.PaperTrading, riskConfig).Value;
        var rsi        = IndicatorConfig.Rsi(14, 70, 30).Value;
        strategy.AddIndicator(rsi);
        return strategy;
    }
}

public sealed class AddRuleCommandHandlerTests
{
    private readonly IStrategyRepository _strategyRepo = Substitute.For<IStrategyRepository>();
    private readonly IUnitOfWork         _unitOfWork   = Substitute.For<IUnitOfWork>();
    private readonly AddRuleCommandHandler _sut;

    public AddRuleCommandHandlerTests()
    {
        _sut = new AddRuleCommandHandler(_strategyRepo, _unitOfWork);
    }

    [Fact]
    public async Task Handle_WhenStrategyNotFound_ReturnsNotFoundError()
    {
        var command = CreateAddRuleCommand(Guid.NewGuid());

        _strategyRepo.GetWithRulesAsync(command.StrategyId, Arg.Any<CancellationToken>())
            .Returns((TradingStrategy?)null);

        var result = await _sut.Handle(command, CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("NOT_FOUND");
    }

    [Fact]
    public async Task Handle_WhenValidRule_AddsAndSaves()
    {
        var strategy = CreateStrategy();
        var command = CreateAddRuleCommand(strategy.Id);

        _strategyRepo.GetWithRulesAsync(strategy.Id, Arg.Any<CancellationToken>())
            .Returns(strategy);

        var result = await _sut.Handle(command, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Rules.Should().ContainSingle();
        await _strategyRepo.Received(1).UpdateAsync(strategy, Arg.Any<CancellationToken>());
        await _unitOfWork.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_WhenEmptyRuleName_ReturnsValidationError()
    {
        var strategy = CreateStrategy();
        var command = new AddRuleCommand(
            strategy.Id,
            Name: "",
            RuleType.Entry,
            ConditionOperator.And,
            [new AddRuleCommand.ConditionItem(IndicatorType.RSI, Comparator.LessThan, 30m)],
            ActionType.BuyMarket,
            50m);

        _strategyRepo.GetWithRulesAsync(strategy.Id, Arg.Any<CancellationToken>())
            .Returns(strategy);

        var result = await _sut.Handle(command, CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("VALIDATION_ERROR");
    }

    [Fact]
    public async Task Handle_WhenZeroAmount_ReturnsValidationError()
    {
        var strategy = CreateStrategy();
        var command = new AddRuleCommand(
            strategy.Id,
            Name: "Invalid Rule",
            RuleType.Entry,
            ConditionOperator.And,
            [new AddRuleCommand.ConditionItem(IndicatorType.RSI, Comparator.LessThan, 30m)],
            ActionType.BuyMarket,
            AmountUsdt: 0m);

        _strategyRepo.GetWithRulesAsync(strategy.Id, Arg.Any<CancellationToken>())
            .Returns(strategy);

        var result = await _sut.Handle(command, CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("VALIDATION_ERROR");
    }

    private static AddRuleCommand CreateAddRuleCommand(Guid strategyId) =>
        new(strategyId,
            Name: "RSI Oversold Entry",
            RuleType.Entry,
            ConditionOperator.And,
            [new AddRuleCommand.ConditionItem(IndicatorType.RSI, Comparator.LessThan, 30m)],
            ActionType.BuyMarket,
            AmountUsdt: 50m);

    private static TradingStrategy CreateStrategy()
    {
        var symbol     = Symbol.Create("BTCUSDT").Value;
        var riskConfig = RiskConfig.Create(100m, 500m, 2m, 4m, 5).Value;
        return TradingStrategy.Create("Test Strategy", symbol, TradingMode.PaperTrading, riskConfig).Value;
    }
}

public sealed class RemoveRuleCommandHandlerTests
{
    private readonly IStrategyRepository _strategyRepo = Substitute.For<IStrategyRepository>();
    private readonly IUnitOfWork         _unitOfWork   = Substitute.For<IUnitOfWork>();
    private readonly RemoveRuleCommandHandler _sut;

    public RemoveRuleCommandHandlerTests()
    {
        _sut = new RemoveRuleCommandHandler(_strategyRepo, _unitOfWork);
    }

    [Fact]
    public async Task Handle_WhenStrategyNotFound_ReturnsNotFoundError()
    {
        var command = new RemoveRuleCommand(Guid.NewGuid(), Guid.NewGuid());

        _strategyRepo.GetWithRulesAsync(command.StrategyId, Arg.Any<CancellationToken>())
            .Returns((TradingStrategy?)null);

        var result = await _sut.Handle(command, CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("NOT_FOUND");
    }

    [Fact]
    public async Task Handle_WhenRuleExists_RemovesAndSaves()
    {
        var strategy = CreateStrategyWithRule(out var ruleId);
        var command = new RemoveRuleCommand(strategy.Id, ruleId);

        _strategyRepo.GetWithRulesAsync(strategy.Id, Arg.Any<CancellationToken>())
            .Returns(strategy);

        var result = await _sut.Handle(command, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Rules.Should().BeEmpty();
        await _unitOfWork.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_WhenRuleDoesNotExist_ReturnsNotFoundError()
    {
        var strategy = CreateStrategyWithRule(out _);
        var command = new RemoveRuleCommand(strategy.Id, Guid.NewGuid());

        _strategyRepo.GetWithRulesAsync(strategy.Id, Arg.Any<CancellationToken>())
            .Returns(strategy);

        var result = await _sut.Handle(command, CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("NOT_FOUND");
    }

    private static TradingStrategy CreateStrategyWithRule(out Guid ruleId)
    {
        var symbol     = Symbol.Create("BTCUSDT").Value;
        var riskConfig = RiskConfig.Create(100m, 500m, 2m, 4m, 5).Value;
        var strategy   = TradingStrategy.Create("Test Strategy", symbol, TradingMode.PaperTrading, riskConfig).Value;

        var condition = RuleCondition.And(
            new LeafCondition(IndicatorType.RSI, Comparator.LessThan, 30m));
        var action = new RuleAction(ActionType.BuyMarket, 50m);
        var rule = TradingRule.Create(strategy.Id, "Entry Rule", RuleType.Entry, condition, action).Value;
        strategy.AddRule(rule);
        ruleId = rule.Id;
        return strategy;
    }
}
