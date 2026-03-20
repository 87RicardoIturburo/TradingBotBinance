using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using TradingBot.Application.RiskManagement;
using TradingBot.Core.Entities;
using TradingBot.Core.Enums;
using TradingBot.Core.Interfaces.Repositories;
using TradingBot.Core.ValueObjects;

namespace TradingBot.Application.Tests.RiskManagement;

public sealed class RiskBudgetServiceTests
{
    private readonly IPositionRepository _positionRepo = Substitute.For<IPositionRepository>();

    private RiskBudgetService CreateService(decimal totalCapital = 500m, decimal maxLossPercent = 10m)
    {
        var config = Options.Create(new RiskBudgetConfig
        {
            TotalCapitalUsdt = totalCapital,
            MaxLossPercent = maxLossPercent
        });
        return new RiskBudgetService(config, _positionRepo, NullLogger<RiskBudgetService>.Instance);
    }

    [Fact]
    public async Task Refresh_WhenNoLoss_LevelIsNormal()
    {
        SetupPnL(realizedPnL: 10m, unrealizedPnL: 5m);
        var sut = CreateService();

        await sut.RefreshAsync();

        sut.CurrentLevel.Should().Be(RiskLevel.Normal);
        sut.AccumulatedLoss.Should().Be(0m);
        sut.OrderAmountMultiplier.Should().Be(1.0m);
        sut.MaxOpenPositionsOverride.Should().BeNull();
    }

    [Fact]
    public async Task Refresh_WhenLossBelow30Percent_LevelIsNormal()
    {
        SetupPnL(realizedPnL: -10m, unrealizedPnL: 0m);
        var sut = CreateService();

        await sut.RefreshAsync();

        sut.CurrentLevel.Should().Be(RiskLevel.Normal);
        sut.AccumulatedLoss.Should().Be(10m);
        sut.BudgetUsedPercent.Should().Be(20m);
    }

    [Fact]
    public async Task Refresh_WhenLoss40Percent_LevelIsReduced()
    {
        SetupPnL(realizedPnL: -20m, unrealizedPnL: 0m);
        var sut = CreateService();

        await sut.RefreshAsync();

        sut.CurrentLevel.Should().Be(RiskLevel.Reduced);
        sut.OrderAmountMultiplier.Should().Be(0.7m);
        sut.MaxOpenPositionsOverride.Should().BeNull();
    }

    [Fact]
    public async Task Refresh_WhenLoss70Percent_LevelIsCritical()
    {
        SetupPnL(realizedPnL: -35m, unrealizedPnL: 0m);
        var sut = CreateService();

        await sut.RefreshAsync();

        sut.CurrentLevel.Should().Be(RiskLevel.Critical);
        sut.OrderAmountMultiplier.Should().Be(0.4m);
        sut.MaxOpenPositionsOverride.Should().Be(1);
    }

    [Fact]
    public async Task Refresh_WhenLoss90Percent_LevelIsCloseOnly()
    {
        SetupPnL(realizedPnL: -45m, unrealizedPnL: 0m);
        var sut = CreateService();

        await sut.RefreshAsync();

        sut.CurrentLevel.Should().Be(RiskLevel.CloseOnly);
        sut.OrderAmountMultiplier.Should().Be(0m);
        sut.MaxOpenPositionsOverride.Should().Be(0);
    }

    [Fact]
    public async Task Refresh_WhenLoss100Percent_LevelIsExhausted()
    {
        SetupPnL(realizedPnL: -50m, unrealizedPnL: 0m);
        var sut = CreateService();

        await sut.RefreshAsync();

        sut.CurrentLevel.Should().Be(RiskLevel.Exhausted);
        sut.OrderAmountMultiplier.Should().Be(0m);
        sut.BudgetUsedPercent.Should().Be(100m);
    }

    [Fact]
    public async Task Refresh_WhenLossExceeds100Percent_LevelIsExhausted()
    {
        SetupPnL(realizedPnL: -70m, unrealizedPnL: 0m);
        var sut = CreateService();

        await sut.RefreshAsync();

        sut.CurrentLevel.Should().Be(RiskLevel.Exhausted);
        sut.BudgetUsedPercent.Should().Be(140m);
    }

    [Fact]
    public async Task Refresh_IncludesUnrealizedPnLInCalculation()
    {
        SetupPnL(realizedPnL: -10m, unrealizedPnL: -30m);
        var sut = CreateService();

        await sut.RefreshAsync();

        sut.AccumulatedLoss.Should().Be(40m);
        sut.CurrentLevel.Should().Be(RiskLevel.CloseOnly);
    }

    [Fact]
    public async Task Refresh_WhenDisabled_AlwaysNormal()
    {
        SetupPnL(realizedPnL: -1000m, unrealizedPnL: 0m);
        var sut = CreateService(totalCapital: 0m);

        await sut.RefreshAsync();

        sut.CurrentLevel.Should().Be(RiskLevel.Normal);
    }

    [Fact]
    public async Task Refresh_WhenProfitable_LossIsZero()
    {
        SetupPnL(realizedPnL: 100m, unrealizedPnL: 50m);
        var sut = CreateService();

        await sut.RefreshAsync();

        sut.AccumulatedLoss.Should().Be(0m);
        sut.CurrentLevel.Should().Be(RiskLevel.Normal);
    }

    private void SetupPnL(decimal realizedPnL, decimal unrealizedPnL)
    {
        var closedPositions = new List<Position>();
        if (realizedPnL != 0m)
        {
            var symbol = Symbol.Create("BTCUSDT").Value;
            var price = Price.Create(50000m).Value;
            var qty = Quantity.Create(1m).Value;
            var position = Position.Open(Guid.NewGuid(), symbol, OrderSide.Buy, price, qty);
            var closePrice = Price.Create(50000m + realizedPnL).Value;
            position.Close(closePrice);
            closedPositions.Add(position);
        }

        _positionRepo.GetClosedByDateRangeAsync(
                Arg.Any<DateTimeOffset>(), Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>())
            .Returns(closedPositions);

        var openPositions = new List<Position>();
        if (unrealizedPnL != 0m)
        {
            var symbol = Symbol.Create("BTCUSDT").Value;
            var entryPrice = Price.Create(50000m).Value;
            var qty = Quantity.Create(1m).Value;
            var position = Position.Open(Guid.NewGuid(), symbol, OrderSide.Buy, entryPrice, qty);
            var currentPrice = Price.Create(50000m + unrealizedPnL).Value;
            position.UpdatePrice(currentPrice);
            openPositions.Add(position);
        }

        _positionRepo.GetOpenPositionsAsync(Arg.Any<CancellationToken>())
            .Returns(openPositions);
    }
}
