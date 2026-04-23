using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using TradingBot.Application.Strategies;
using TradingBot.Core.Enums;
using TradingBot.Core.ValueObjects;

namespace TradingBot.Application.Tests.Strategies;

public sealed class StrategyResolverTests
{
    [Theory]
    [InlineData(MarketRegime.Trending)]
    [InlineData(MarketRegime.Ranging)]
    [InlineData(MarketRegime.Bearish)]
    [InlineData(MarketRegime.Unknown)]
    public void Resolve_WhenOperableRegime_ReturnsStrategy(MarketRegime regime)
    {
        var resolver = CreateResolver();
        var config = CreateConfig();

        var result = resolver.Resolve(Guid.NewGuid(), regime, config);

        result.Should().NotBeNull();
    }

    [Theory]
    [InlineData(MarketRegime.Indefinite)]
    [InlineData(MarketRegime.HighVolatility)]
    public void Resolve_WhenNonOperableRegime_ReturnsNull(MarketRegime regime)
    {
        var resolver = CreateResolver();
        var config = CreateConfig();

        var result = resolver.Resolve(Guid.NewGuid(), regime, config);

        result.Should().BeNull();
    }

    [Fact]
    public void Resolve_SameStrategyId_ReturnsCachedInstances()
    {
        var resolver = CreateResolver();
        var config = CreateConfig();
        var strategyId = Guid.NewGuid();

        var first = resolver.Resolve(strategyId, MarketRegime.Trending, config);
        var second = resolver.Resolve(strategyId, MarketRegime.Trending, config);

        first.Should().BeSameAs(second);
    }

    [Fact]
    public void Resolve_SameStrategyIdDifferentRegime_ReturnsDifferentInstances()
    {
        var resolver = CreateResolver();
        var config = CreateConfig();
        var strategyId = Guid.NewGuid();

        var trending = resolver.Resolve(strategyId, MarketRegime.Trending, config);
        var ranging = resolver.Resolve(strategyId, MarketRegime.Ranging, config);

        trending.Should().NotBeSameAs(ranging);
    }

    [Fact]
    public void Resolve_Trending_ReturnsTrendingStrategy()
    {
        var resolver = CreateResolver();
        var config = CreateConfig();

        var result = resolver.Resolve(Guid.NewGuid(), MarketRegime.Trending, config);

        result.Should().BeOfType<TrendingTradingStrategy>();
    }

    [Fact]
    public void Resolve_Ranging_ReturnsRangingStrategy()
    {
        var resolver = CreateResolver();
        var config = CreateConfig();

        var result = resolver.Resolve(Guid.NewGuid(), MarketRegime.Ranging, config);

        result.Should().BeOfType<RangingTradingStrategy>();
    }

    [Fact]
    public void Resolve_Bearish_ReturnsBearishStrategy()
    {
        var resolver = CreateResolver();
        var config = CreateConfig();

        var result = resolver.Resolve(Guid.NewGuid(), MarketRegime.Bearish, config);

        result.Should().BeOfType<BearishTradingStrategy>();
    }

    private static StrategyResolver CreateResolver()
    {
        var sp = NSubstitute.Substitute.For<IServiceProvider>();
        sp.GetService(typeof(DefaultTradingStrategy))
            .Returns(_ => new DefaultTradingStrategy(NullLogger<DefaultTradingStrategy>.Instance));
        sp.GetService(typeof(TrendingTradingStrategy))
            .Returns(_ => new TrendingTradingStrategy(NullLogger<DefaultTradingStrategy>.Instance));
        sp.GetService(typeof(RangingTradingStrategy))
            .Returns(_ => new RangingTradingStrategy(NullLogger<DefaultTradingStrategy>.Instance));
        sp.GetService(typeof(BearishTradingStrategy))
            .Returns(_ => new BearishTradingStrategy(NullLogger<DefaultTradingStrategy>.Instance));
        return new StrategyResolver(sp);
    }

    private static TradingBot.Core.Entities.TradingStrategy CreateConfig()
    {
        var symbol = Symbol.Create("BTCUSDT").Value;
        var risk = RiskConfig.Create(100m, 500m, 2m, 4m).Value;
        return TradingBot.Core.Entities.TradingStrategy.Create("Test", symbol, TradingMode.PaperTrading, risk).Value;
    }
}
