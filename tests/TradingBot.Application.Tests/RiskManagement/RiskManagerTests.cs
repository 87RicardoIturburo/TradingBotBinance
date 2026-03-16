using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using TradingBot.Application.RiskManagement;
using TradingBot.Core.Common;
using TradingBot.Core.Entities;
using TradingBot.Core.Enums;
using TradingBot.Core.Interfaces.Repositories;
using TradingBot.Core.Interfaces.Services;
using TradingBot.Core.ValueObjects;

namespace TradingBot.Application.Tests.RiskManagement;

public sealed class RiskManagerTests
{
    private readonly IStrategyRepository  _strategyRepo  = Substitute.For<IStrategyRepository>();
    private readonly IPositionRepository  _positionRepo  = Substitute.For<IPositionRepository>();
    private readonly IAccountService      _accountService = Substitute.For<IAccountService>();
    private readonly PortfolioRiskManager _portfolioRisk;
    private readonly RiskManager          _sut;

    public RiskManagerTests()
    {
        _portfolioRisk = new PortfolioRiskManager(_positionRepo);
        var globalRisk = Options.Create(new GlobalRiskSettings());
        _sut = new RiskManager(_strategyRepo, _positionRepo, _accountService, globalRisk, _portfolioRisk, NullLogger<RiskManager>.Instance);
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
        _positionRepo.GetTradeStatsAsync(strategyId, Arg.Any<CancellationToken>())
            .Returns((0, 0, 0m, 0m)); // Not enough trades → expectancy skipped

        var result = await _sut.ValidateOrderAsync(order);

        result.IsSuccess.Should().BeTrue();
    }

    // ── Mathematical Expectancy ───────────────────────────────────────────

    [Fact]
    public async Task ValidateOrder_WhenExpectancyPositive_ReturnsSuccess()
    {
        var strategyId = Guid.NewGuid();
        var strategy   = CreateStrategy(strategyId);
        var order      = CreateOrder(strategyId, quantity: 0.001m, limitPrice: 50m);

        SetupPassingChecks(strategyId, strategy);
        // 35 trades: 21 wins, 14 losses; avgWin = 20, avgLoss = 10
        // E = (21/35 * 20) - (14/35 * 10) = 12 - 4 = 8.0
        _positionRepo.GetTradeStatsAsync(strategyId, Arg.Any<CancellationToken>())
            .Returns((35, 21, 420m, 140m));

        var result = await _sut.ValidateOrderAsync(order);

        result.IsSuccess.Should().BeTrue();
    }

    [Fact]
    public async Task ValidateOrder_WhenExpectancyNegative_ReturnsRiskLimitExceeded()
    {
        var strategyId = Guid.NewGuid();
        var strategy   = CreateStrategy(strategyId);
        var order      = CreateOrder(strategyId, quantity: 0.001m, limitPrice: 50m);

        SetupPassingChecks(strategyId, strategy);
        // 36 trades: 9 wins, 27 losses; avgWin = 10, avgLoss = 20
        // E = (9/36 * 10) - (27/36 * 20) = 2.5 - 15.0 = -12.5
        _positionRepo.GetTradeStatsAsync(strategyId, Arg.Any<CancellationToken>())
            .Returns((36, 9, 90m, 540m));

        var result = await _sut.ValidateOrderAsync(order);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("RISK_LIMIT_EXCEEDED");
        result.Error.Message.Should().Contain("esperanza matemática");
    }

    [Fact]
    public async Task ValidateOrder_WhenExpectancyZero_ReturnsRiskLimitExceeded()
    {
        var strategyId = Guid.NewGuid();
        var strategy   = CreateStrategy(strategyId);
        var order      = CreateOrder(strategyId, quantity: 0.001m, limitPrice: 50m);

        SetupPassingChecks(strategyId, strategy);
        // 30 trades: 15 wins, 15 losses; avgWin = 10, avgLoss = 10 → E = 0
        _positionRepo.GetTradeStatsAsync(strategyId, Arg.Any<CancellationToken>())
            .Returns((30, 15, 150m, 150m));

        var result = await _sut.ValidateOrderAsync(order);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("RISK_LIMIT_EXCEEDED");
    }

    [Fact]
    public async Task ValidateOrder_WhenNotEnoughTradesForExpectancy_SkipsCheck()
    {
        var strategyId = Guid.NewGuid();
        var strategy   = CreateStrategy(strategyId);
        var order      = CreateOrder(strategyId, quantity: 0.001m, limitPrice: 50m);

        SetupPassingChecks(strategyId, strategy);
        // Only 20 trades (below minimum of 30) → expectancy check skipped
        _positionRepo.GetTradeStatsAsync(strategyId, Arg.Any<CancellationToken>())
            .Returns((20, 3, 30m, 340m)); // Would be very negative, but skipped

        var result = await _sut.ValidateOrderAsync(order);

        result.IsSuccess.Should().BeTrue();
    }

    // ── GetMathematicalExpectancyAsync ─────────────────────────────────────

    [Fact]
    public async Task GetMathematicalExpectancy_WithSufficientTrades_ReturnsCorrectValue()
    {
        var strategyId = Guid.NewGuid();
        // 35 trades: 21 wins (total $420), 14 losses (total $140)
        // WinRate = 0.6, AvgWin = 20, LossRate = 0.4, AvgLoss = 10
        // E = 0.6 * 20 - 0.4 * 10 = 12 - 4 = 8
        _positionRepo.GetTradeStatsAsync(strategyId, Arg.Any<CancellationToken>())
            .Returns((35, 21, 420m, 140m));

        var result = await _sut.GetMathematicalExpectancyAsync(strategyId);

        result.Should().Be(8m);
    }

    [Fact]
    public async Task GetMathematicalExpectancy_WithFewerTradesThanMinimum_ReturnsNull()
    {
        var strategyId = Guid.NewGuid();
        _positionRepo.GetTradeStatsAsync(strategyId, Arg.Any<CancellationToken>())
            .Returns((RiskManager.MinTradesForExpectancy - 1, 3, 30m, 20m));

        var result = await _sut.GetMathematicalExpectancyAsync(strategyId);

        result.Should().BeNull();
    }

    [Fact]
    public async Task GetMathematicalExpectancy_WithNoTrades_ReturnsNull()
    {
        var strategyId = Guid.NewGuid();
        _positionRepo.GetTradeStatsAsync(strategyId, Arg.Any<CancellationToken>())
            .Returns((0, 0, 0m, 0m));

        var result = await _sut.GetMathematicalExpectancyAsync(strategyId);

        result.Should().BeNull();
    }

    // ── Balance Validation (Paso 16) ─────────────────────────────────────

    [Fact]
    public async Task ValidateOrder_WhenLiveBalanceInsufficient_ReturnsRiskLimitExceeded()
    {
        var strategyId = Guid.NewGuid();
        var strategy   = CreateStrategy(strategyId);
        // Live order: qty=1 * price=100 = 100 USDT + 5% buffer = 105 USDT required
        var order      = CreateLiveOrder(strategyId, quantity: 1m, limitPrice: 100m);

        _strategyRepo.GetByIdAsync(strategyId, Arg.Any<CancellationToken>())
            .Returns(strategy);
        _accountService.GetAvailableBalanceAsync("USDT", Arg.Any<CancellationToken>())
            .Returns(Result<decimal, DomainError>.Success(50m)); // Only 50 USDT available
        _positionRepo.GetDailyRealizedPnLAsync(strategyId, Arg.Any<CancellationToken>())
            .Returns(0m);
        _positionRepo.GetOpenPositionCountAsync(strategyId, Arg.Any<CancellationToken>())
            .Returns(0);

        var result = await _sut.ValidateOrderAsync(order);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("RISK_LIMIT_EXCEEDED");
        result.Error.Message.Should().Contain("Saldo insuficiente");
    }

    [Fact]
    public async Task ValidateOrder_WhenLiveBalanceSufficient_Passes()
    {
        var strategyId = Guid.NewGuid();
        var strategy   = CreateStrategy(strategyId);
        var order      = CreateLiveOrder(strategyId, quantity: 0.001m, limitPrice: 50m);

        _strategyRepo.GetByIdAsync(strategyId, Arg.Any<CancellationToken>())
            .Returns(strategy);
        _accountService.GetAvailableBalanceAsync("USDT", Arg.Any<CancellationToken>())
            .Returns(Result<decimal, DomainError>.Success(1000m));
        _positionRepo.GetDailyRealizedPnLAsync(strategyId, Arg.Any<CancellationToken>())
            .Returns(0m);
        _positionRepo.GetOpenPositionCountAsync(strategyId, Arg.Any<CancellationToken>())
            .Returns(0);
        _positionRepo.GetTradeStatsAsync(strategyId, Arg.Any<CancellationToken>())
            .Returns((0, 0, 0m, 0m));

        var result = await _sut.ValidateOrderAsync(order);

        result.IsSuccess.Should().BeTrue();
    }

    [Fact]
    public async Task ValidateOrder_WhenPaperTrade_SkipsBalanceCheck()
    {
        var strategyId = Guid.NewGuid();
        var strategy   = CreateStrategy(strategyId);
        var order      = CreateOrder(strategyId, quantity: 0.001m, limitPrice: 50m);

        SetupPassingChecks(strategyId, strategy);
        _positionRepo.GetTradeStatsAsync(strategyId, Arg.Any<CancellationToken>())
            .Returns((0, 0, 0m, 0m));

        var result = await _sut.ValidateOrderAsync(order);

        result.IsSuccess.Should().BeTrue();
        // Balance service should NOT be called for paper trades
        await _accountService.DidNotReceive()
            .GetAvailableBalanceAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    // ── Global Risk Limits ───────────────────────────────────────────────

    [Fact]
    public async Task ValidateOrder_WhenMarketOrderExceedsMax_BlocksWithEstimatedPrice()
    {
        var strategyId = Guid.NewGuid();
        var strategy   = CreateStrategy(strategyId, maxOrderAmount: 50m);
        // Market order: qty=0.001 @ estimated price 60000 = 60 USDT > max 50
        var order      = CreateOrderWithEstimatedPrice(strategyId, quantity: 0.001m, estimatedPrice: 60000m);

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
    public async Task ValidateOrder_WhenMarketOrderWithinMax_Passes()
    {
        var strategyId = Guid.NewGuid();
        var strategy   = CreateStrategy(strategyId, maxOrderAmount: 100m);
        // Market order: qty=0.001 @ estimated price 60000 = 60 USDT < max 100
        var order      = CreateOrderWithEstimatedPrice(strategyId, quantity: 0.001m, estimatedPrice: 60000m);

        SetupPassingChecks(strategyId, strategy);
        _positionRepo.GetTradeStatsAsync(strategyId, Arg.Any<CancellationToken>())
            .Returns((0, 0, 0m, 0m));

        var result = await _sut.ValidateOrderAsync(order);

        result.IsSuccess.Should().BeTrue();
    }

    [Fact]
    public async Task ValidateOrder_WhenGlobalDailyLossExceeded_ReturnsKillSwitch()
    {
        var globalRisk = Options.Create(new GlobalRiskSettings
        {
            MaxDailyLossUsdt = 500,
            MaxGlobalOpenPositions = 0 // disabled
        });
        var sut = new RiskManager(_strategyRepo, _positionRepo, _accountService, globalRisk, _portfolioRisk, NullLogger<RiskManager>.Instance);

        var strategyId = Guid.NewGuid();
        var strategy   = CreateStrategy(strategyId);
        var order      = CreateOrder(strategyId, quantity: 0.001m, limitPrice: 50m);

        SetupPassingChecks(strategyId, strategy);
        _positionRepo.GetTradeStatsAsync(strategyId, Arg.Any<CancellationToken>())
            .Returns((0, 0, 0m, 0m));

        // Simulate global loss: closed positions across all strategies today
        var today = new DateTimeOffset(DateTimeOffset.UtcNow.Date, TimeSpan.Zero);
        _positionRepo.GetClosedByDateRangeAsync(
                Arg.Any<DateTimeOffset>(), Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>())
            .Returns(new List<Position>
            {
                CreateClosedPosition(-300m),
                CreateClosedPosition(-250m)
            });

        var result = await sut.ValidateOrderAsync(order);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("RISK_LIMIT_EXCEEDED");
        result.Error.Message.Should().Contain("GLOBAL");
    }

    [Fact]
    public async Task ValidateOrder_WhenGlobalOpenPositionsExceeded_ReturnsLimitExceeded()
    {
        var globalRisk = Options.Create(new GlobalRiskSettings
        {
            MaxDailyLossUsdt = 0, // disabled
            MaxGlobalOpenPositions = 2
        });
        var sut = new RiskManager(_strategyRepo, _positionRepo, _accountService, globalRisk, _portfolioRisk, NullLogger<RiskManager>.Instance);

        var strategyId = Guid.NewGuid();
        var strategy   = CreateStrategy(strategyId);
        var order      = CreateOrder(strategyId, quantity: 0.001m, limitPrice: 50m);

        SetupPassingChecks(strategyId, strategy);
        _positionRepo.GetTradeStatsAsync(strategyId, Arg.Any<CancellationToken>())
            .Returns((0, 0, 0m, 0m));

        // 2 open positions globally
        _positionRepo.GetOpenPositionsAsync(Arg.Any<CancellationToken>())
            .Returns(new List<Position>
            {
                CreateOpenPosition(),
                CreateOpenPosition()
            });

        var result = await sut.ValidateOrderAsync(order);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("RISK_LIMIT_EXCEEDED");
        result.Error.Message.Should().Contain("globales");
    }

    [Fact]
    public async Task ValidateOrder_WhenGlobalLimitsDisabled_Passes()
    {
        var globalRisk = Options.Create(new GlobalRiskSettings());
        var sut = new RiskManager(_strategyRepo, _positionRepo, _accountService, globalRisk, _portfolioRisk, NullLogger<RiskManager>.Instance);

        var strategyId = Guid.NewGuid();
        var strategy   = CreateStrategy(strategyId);
        var order      = CreateOrder(strategyId, quantity: 0.001m, limitPrice: 50m);

        SetupPassingChecks(strategyId, strategy);
        _positionRepo.GetTradeStatsAsync(strategyId, Arg.Any<CancellationToken>())
            .Returns((0, 0, 0m, 0m));

        var result = await sut.ValidateOrderAsync(order);

        result.IsSuccess.Should().BeTrue();
    }

    // ── Helpers ────────────────────────────────────────────────────────────

    private void SetupPassingChecks(Guid strategyId, TradingStrategy strategy)
    {
        _strategyRepo.GetByIdAsync(strategyId, Arg.Any<CancellationToken>())
            .Returns(strategy);
        _positionRepo.GetDailyRealizedPnLAsync(strategyId, Arg.Any<CancellationToken>())
            .Returns(0m);
        _positionRepo.GetOpenPositionCountAsync(strategyId, Arg.Any<CancellationToken>())
            .Returns(0);
    }

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

    private static Order CreateOrderWithEstimatedPrice(
        Guid? strategyId = null,
        decimal quantity = 0.01m,
        decimal estimatedPrice = 50000m)
    {
        var symbol = Symbol.Create("BTCUSDT").Value;
        var qty    = Quantity.Create(quantity).Value;
        var estimated = Price.Create(estimatedPrice).Value;

        return Order.Create(
            strategyId ?? Guid.NewGuid(), symbol, OrderSide.Buy, OrderType.Market,
            qty, TradingMode.PaperTrading, estimatedPrice: estimated).Value;
    }

    private static Order CreateLiveOrder(
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
            qty, TradingMode.Live, limit).Value;
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

    private static Position CreateClosedPosition(decimal pnl)
    {
        var symbol   = Symbol.Create("BTCUSDT").Value;
        var price    = Price.Create(50000m).Value;
        var qty      = Quantity.Create(1m).Value;
        var position = Position.Open(Guid.NewGuid(), symbol, OrderSide.Buy, price, qty);
        // closePrice = entryPrice + pnl/qty → ensures valid positive price for reasonable pnl
        var closePrice = Price.Create(50000m + pnl).Value;
        position.Close(closePrice);
        return position;
    }

    private static Position CreateOpenPosition()
    {
        var symbol = Symbol.Create("BTCUSDT").Value;
        var price  = Price.Create(100m).Value;
        var qty    = Quantity.Create(0.01m).Value;
        return Position.Open(Guid.NewGuid(), symbol, OrderSide.Buy, price, qty);
    }
}
