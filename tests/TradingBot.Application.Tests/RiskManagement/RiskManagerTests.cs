using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using TradingBot.Application.RiskManagement;
using TradingBot.Core.Entities;
using TradingBot.Core.Enums;
using TradingBot.Core.Interfaces.Repositories;
using TradingBot.Core.ValueObjects;

namespace TradingBot.Application.Tests.RiskManagement;

public sealed class RiskManagerTests
{
    private readonly IStrategyRepository  _strategyRepo  = Substitute.For<IStrategyRepository>();
    private readonly IPositionRepository  _positionRepo  = Substitute.For<IPositionRepository>();
    private readonly RiskManager          _sut;

    public RiskManagerTests()
    {
        _sut = new RiskManager(_strategyRepo, _positionRepo, NullLogger<RiskManager>.Instance);
    }

    [Fact]
    public async Task ValidateOrder_WhenStrategyNotFound_ReturnsNotFoundError()
    {
        var order = CreateOrder();
        _strategyRepo.GetByIdAsync(order.StrategyId, Arg.Any<CancellationToken>())
            .Returns((TradingStrategy?)null);

        var result = await _sut.ValidateOrderAsync(order);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("NOT_FOUND");
    }

    [Fact]
    public async Task ValidateOrder_WhenAmountExceedsMax_ReturnsRiskLimitExceeded()
    {
        var strategyId = Guid.NewGuid();
        var strategy   = CreateStrategy(strategyId, maxOrderAmount: 50m);
        var order      = CreateOrder(strategyId, quantity: 1m, limitPrice: 60m);

        _strategyRepo.GetByIdAsync(strategyId, Arg.Any<CancellationToken>())
            .Returns(strategy);
        _positionRepo.GetDailyRealizedPnLAsync(strategyId, Arg.Any<CancellationToken>())
            .Returns(0m);
        _positionRepo.GetOpenPositionCountAsync(strategyId, Arg.Any<CancellationToken>())
            .Returns(0);

        var result = await _sut.ValidateOrderAsync(order);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("RISK_LIMIT_EXCEEDED");
    }

    [Fact]
    public async Task ValidateOrder_WhenDailyLossExceeded_ReturnsRiskLimitExceeded()
    {
        var strategyId = Guid.NewGuid();
        var strategy   = CreateStrategy(strategyId, maxDailyLoss: 100m);
        var order      = CreateOrder(strategyId, quantity: 0.001m, limitPrice: 10m);

        _strategyRepo.GetByIdAsync(strategyId, Arg.Any<CancellationToken>())
            .Returns(strategy);
        _positionRepo.GetDailyRealizedPnLAsync(strategyId, Arg.Any<CancellationToken>())
            .Returns(-100m);
        _positionRepo.GetOpenPositionCountAsync(strategyId, Arg.Any<CancellationToken>())
            .Returns(0);

        var result = await _sut.ValidateOrderAsync(order);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("RISK_LIMIT_EXCEEDED");
    }

    [Fact]
    public async Task ValidateOrder_WhenMaxPositionsReached_ReturnsRiskLimitExceeded()
    {
        var strategyId = Guid.NewGuid();
        var strategy   = CreateStrategy(strategyId, maxOpenPositions: 2);
        var order      = CreateOrder(strategyId, quantity: 0.001m, limitPrice: 10m);

        _strategyRepo.GetByIdAsync(strategyId, Arg.Any<CancellationToken>())
            .Returns(strategy);
        _positionRepo.GetDailyRealizedPnLAsync(strategyId, Arg.Any<CancellationToken>())
            .Returns(0m);
        _positionRepo.GetOpenPositionCountAsync(strategyId, Arg.Any<CancellationToken>())
            .Returns(2);

        var result = await _sut.ValidateOrderAsync(order);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("RISK_LIMIT_EXCEEDED");
    }

    [Fact]
    public async Task ValidateOrder_WhenAllLimitsWithinRange_ReturnsSuccess()
    {
        var strategyId = Guid.NewGuid();
        var strategy   = CreateStrategy(strategyId);
        var order      = CreateOrder(strategyId, quantity: 0.001m, limitPrice: 50m);

        _strategyRepo.GetByIdAsync(strategyId, Arg.Any<CancellationToken>())
            .Returns(strategy);
        _positionRepo.GetDailyRealizedPnLAsync(strategyId, Arg.Any<CancellationToken>())
            .Returns(-10m);
        _positionRepo.GetOpenPositionCountAsync(strategyId, Arg.Any<CancellationToken>())
            .Returns(1);

        var result = await _sut.ValidateOrderAsync(order);

        result.IsSuccess.Should().BeTrue();
    }

    // ── Helpers ────────────────────────────────────────────────────────────

    private static Order CreateOrder(
        Guid? strategyId = null,
        decimal quantity = 0.01m,
        decimal? limitPrice = null)
    {
        var symbol = Symbol.Create("BTCUSDT").Value;
        var qty    = Quantity.Create(quantity).Value;
        var limit  = limitPrice.HasValue ? Price.Create(limitPrice.Value).Value : null;
        var type   = limitPrice.HasValue ? OrderType.Limit : OrderType.Market;

        return Order.Create(
            strategyId ?? Guid.NewGuid(), symbol, OrderSide.Buy, type,
            qty, TradingMode.PaperTrading, limit).Value;
    }

    private static TradingStrategy CreateStrategy(
        Guid? id = null,
        decimal maxOrderAmount = 100m,
        decimal maxDailyLoss = 500m,
        int maxOpenPositions = 3)
    {
        var symbol     = Symbol.Create("BTCUSDT").Value;
        var riskConfig = RiskConfig.Create(maxOrderAmount, maxDailyLoss, 2m, 4m, maxOpenPositions).Value;
        return TradingStrategy.Create("Test Strategy", symbol, TradingMode.PaperTrading, riskConfig).Value;
    }
}
