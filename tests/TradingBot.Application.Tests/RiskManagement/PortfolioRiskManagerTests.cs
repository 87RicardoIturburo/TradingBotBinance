using FluentAssertions;
using NSubstitute;
using TradingBot.Application.RiskManagement;
using TradingBot.Core.Entities;
using TradingBot.Core.Enums;
using TradingBot.Core.Interfaces.Repositories;
using TradingBot.Core.ValueObjects;

namespace TradingBot.Application.Tests.RiskManagement;

public sealed class PortfolioRiskManagerTests
{
    private readonly IPositionRepository _positionRepo = Substitute.For<IPositionRepository>();
    private readonly PortfolioRiskManager _sut;

    public PortfolioRiskManagerTests()
    {
        _sut = new PortfolioRiskManager(_positionRepo);
    }

    // ── GetPortfolioExposureAsync ──────────────────────────────────────────

    [Fact]
    public async Task GetPortfolioExposureAsync_WhenNoPositions_ReturnsZeros()
    {
        _positionRepo.GetOpenPositionsAsync(Arg.Any<CancellationToken>())
            .Returns(new List<Position>());

        var exposure = await _sut.GetPortfolioExposureAsync();

        exposure.TotalLongUsdt.Should().Be(0m);
        exposure.TotalShortUsdt.Should().Be(0m);
        exposure.NetUsdt.Should().Be(0m);
    }

    [Fact]
    public async Task GetPortfolioExposureAsync_WhenMixedPositions_CalculatesCorrectly()
    {
        _positionRepo.GetOpenPositionsAsync(Arg.Any<CancellationToken>())
            .Returns(new List<Position>
            {
                CreateOpenPosition("BTCUSDT", OrderSide.Buy, 50000m, 0.1m),   // 5000 USDT long
                CreateOpenPosition("ETHUSDT", OrderSide.Buy, 3000m, 1m),      // 3000 USDT long
                CreateOpenPosition("BTCUSDT", OrderSide.Sell, 50000m, 0.05m), // 2500 USDT short
            });

        var exposure = await _sut.GetPortfolioExposureAsync();

        exposure.TotalLongUsdt.Should().Be(8000m);
        exposure.TotalShortUsdt.Should().Be(2500m);
        exposure.NetUsdt.Should().Be(5500m);
        exposure.TotalUsdt.Should().Be(10500m);
    }

    // ── GetExposureBySymbolAsync ───────────────────────────────────────────

    [Fact]
    public async Task GetExposureBySymbolAsync_GroupsCorrectlyBySymbol()
    {
        _positionRepo.GetOpenPositionsAsync(Arg.Any<CancellationToken>())
            .Returns(new List<Position>
            {
                CreateOpenPosition("BTCUSDT", OrderSide.Buy, 50000m, 0.1m),   // BTC: 5000 long
                CreateOpenPosition("BTCUSDT", OrderSide.Sell, 50000m, 0.04m), // BTC: 2000 short
                CreateOpenPosition("ETHUSDT", OrderSide.Buy, 3000m, 2m),      // ETH: 6000 long
            });

        var bySymbol = await _sut.GetExposureBySymbolAsync();

        bySymbol.Should().HaveCount(2);
        bySymbol["BTCUSDT"].LongUsdt.Should().Be(5000m);
        bySymbol["BTCUSDT"].ShortUsdt.Should().Be(2000m);
        bySymbol["BTCUSDT"].NetUsdt.Should().Be(3000m);
        bySymbol["ETHUSDT"].LongUsdt.Should().Be(6000m);
        bySymbol["ETHUSDT"].ShortUsdt.Should().Be(0m);
    }

    // ── ValidateExposureAsync ─────────────────────────────────────────────

    [Fact]
    public async Task ValidateExposureAsync_WhenLongExposureExceedsLimit_Blocks()
    {
        _positionRepo.GetOpenPositionsAsync(Arg.Any<CancellationToken>())
            .Returns(new List<Position>
            {
                CreateOpenPosition("BTCUSDT", OrderSide.Buy, 50000m, 0.18m), // 9000 USDT long
            });

        var settings = new GlobalRiskSettings { MaxPortfolioLongExposureUsdt = 10000m };
        var order = CreateBuyOrder("ETHUSDT", 1m, 2000m); // +2000 → 11000 total long

        var result = await _sut.ValidateExposureAsync(order, settings);

        result.IsAllowed.Should().BeFalse();
        result.Reason.Should().Contain("Long");
    }

    [Fact]
    public async Task ValidateExposureAsync_WhenLongExposureWithinLimit_Passes()
    {
        _positionRepo.GetOpenPositionsAsync(Arg.Any<CancellationToken>())
            .Returns(new List<Position>
            {
                CreateOpenPosition("BTCUSDT", OrderSide.Buy, 50000m, 0.1m), // 5000 USDT long
            });

        var settings = new GlobalRiskSettings { MaxPortfolioLongExposureUsdt = 10000m };
        var order = CreateBuyOrder("ETHUSDT", 1m, 3000m); // +3000 → 8000 total long

        var result = await _sut.ValidateExposureAsync(order, settings);

        result.IsAllowed.Should().BeTrue();
    }

    [Fact]
    public async Task ValidateExposureAsync_WhenShortExposureExceedsLimit_Blocks()
    {
        _positionRepo.GetOpenPositionsAsync(Arg.Any<CancellationToken>())
            .Returns(new List<Position>
            {
                CreateOpenPosition("BTCUSDT", OrderSide.Sell, 50000m, 0.1m), // 5000 short
            });

        var settings = new GlobalRiskSettings { MaxPortfolioShortExposureUsdt = 5000m };
        var order = CreateSellOrder("ETHUSDT", 1m, 1000m); // +1000 → 6000 total short

        var result = await _sut.ValidateExposureAsync(order, settings);

        result.IsAllowed.Should().BeFalse();
        result.Reason.Should().Contain("Short");
    }

    [Fact]
    public async Task ValidateExposureAsync_WhenSymbolConcentrationExceedsLimit_Blocks()
    {
        _positionRepo.GetOpenPositionsAsync(Arg.Any<CancellationToken>())
            .Returns(new List<Position>
            {
                CreateOpenPosition("BTCUSDT", OrderSide.Buy, 50000m, 0.1m), // BTC: 5000
                CreateOpenPosition("ETHUSDT", OrderSide.Buy, 3000m, 1m),    // ETH: 3000
            });

        // Max 60% per symbol. BTC is at 5000/8000=62.5% already.
        // New BTC order of 2000 → BTC=7000, total=10000 → 70% > 60%
        var settings = new GlobalRiskSettings { MaxExposurePerSymbolPercent = 60m };
        var order = CreateBuyOrder("BTCUSDT", 0.04m, 50000m); // 2000 USDT

        var result = await _sut.ValidateExposureAsync(order, settings);

        result.IsAllowed.Should().BeFalse();
        result.Reason.Should().Contain("Concentración");
        result.Reason.Should().Contain("BTCUSDT");
    }

    [Fact]
    public async Task ValidateExposureAsync_WhenAllLimitsDisabled_Passes()
    {
        _positionRepo.GetOpenPositionsAsync(Arg.Any<CancellationToken>())
            .Returns(new List<Position>
            {
                CreateOpenPosition("BTCUSDT", OrderSide.Buy, 50000m, 10m), // 500000 USDT
            });

        var settings = new GlobalRiskSettings(); // all limits 0 = disabled
        var order = CreateBuyOrder("BTCUSDT", 10m, 50000m);

        var result = await _sut.ValidateExposureAsync(order, settings);

        result.IsAllowed.Should().BeTrue();
    }

    [Fact]
    public async Task ValidateExposureAsync_WhenSellOrderAndLongLimit_DoesNotBlock()
    {
        _positionRepo.GetOpenPositionsAsync(Arg.Any<CancellationToken>())
            .Returns(new List<Position>());

        var settings = new GlobalRiskSettings { MaxPortfolioLongExposureUsdt = 100m };
        var order = CreateSellOrder("BTCUSDT", 1m, 50000m); // Sell does NOT check long limit

        var result = await _sut.ValidateExposureAsync(order, settings);

        result.IsAllowed.Should().BeTrue();
    }

    // ── CalculateExposureBySymbol (static) ────────────────────────────────

    [Fact]
    public void CalculateExposureBySymbol_WhenEmpty_ReturnsEmpty()
    {
        var result = PortfolioRiskManager.CalculateExposureBySymbol([]);

        result.Should().BeEmpty();
    }

    [Fact]
    public void CalculateExposureBySymbol_AccumulatesMultiplePositionsSameSymbol()
    {
        var positions = new List<Position>
        {
            CreateOpenPosition("BTCUSDT", OrderSide.Buy, 40000m, 0.1m),
            CreateOpenPosition("BTCUSDT", OrderSide.Buy, 42000m, 0.2m),
        };

        var result = PortfolioRiskManager.CalculateExposureBySymbol(positions);

        result["BTCUSDT"].LongUsdt.Should().Be(4000m + 8400m);
        result["BTCUSDT"].ShortUsdt.Should().Be(0m);
    }

    // ── Helpers ────────────────────────────────────────────────────────────

    private static Position CreateOpenPosition(string symbol, OrderSide side, decimal price, decimal qty)
    {
        return Position.Open(
            Guid.NewGuid(),
            Symbol.Create(symbol).Value,
            side,
            Price.Create(price).Value,
            Quantity.Create(qty).Value);
    }

    private static Order CreateBuyOrder(string symbol, decimal qty, decimal limitPrice)
    {
        return Order.Create(
            Guid.NewGuid(),
            Symbol.Create(symbol).Value,
            OrderSide.Buy,
            OrderType.Limit,
            Quantity.Create(qty).Value,
            TradingMode.PaperTrading,
            Price.Create(limitPrice).Value).Value;
    }

    private static Order CreateSellOrder(string symbol, decimal qty, decimal limitPrice)
    {
        return Order.Create(
            Guid.NewGuid(),
            Symbol.Create(symbol).Value,
            OrderSide.Sell,
            OrderType.Limit,
            Quantity.Create(qty).Value,
            TradingMode.PaperTrading,
            Price.Create(limitPrice).Value).Value;
    }
}
