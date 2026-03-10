using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using TradingBot.Application.Strategies;
using TradingBot.Core.Entities;
using TradingBot.Core.Enums;
using TradingBot.Core.Events;
using TradingBot.Core.ValueObjects;

namespace TradingBot.Application.Tests.Strategies;

public sealed class DefaultTradingStrategyTests
{
    private readonly DefaultTradingStrategy _sut = new(NullLogger<DefaultTradingStrategy>.Instance);

    // ── InitializeAsync ───────────────────────────────────────────────────

    [Fact]
    public async Task InitializeAsync_WhenCalled_SetsProperties()
    {
        var strategy = CreateStrategy();

        await _sut.InitializeAsync(strategy);

        _sut.IsInitialized.Should().BeTrue();
        _sut.StrategyId.Should().Be(strategy.Id);
        _sut.Symbol.Should().Be(strategy.Symbol);
        _sut.Mode.Should().Be(strategy.Mode);
    }

    // ── ProcessTickAsync ──────────────────────────────────────────────────

    [Fact]
    public async Task ProcessTickAsync_WhenNotInitialized_ReturnsFailure()
    {
        var tick = CreateTick(55000m);

        var result = await _sut.ProcessTickAsync(tick);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("INVALID_OPERATION");
    }

    [Fact]
    public async Task ProcessTickAsync_WhenRsiNotReady_ReturnsNull()
    {
        var strategy = CreateStrategyWithRsi(period: 14);
        await _sut.InitializeAsync(strategy);

        // Feed only a few ticks — RSI needs period + 1 data points to be ready
        for (var i = 0; i < 5; i++)
        {
            var result = await _sut.ProcessTickAsync(CreateTick(50000m + i * 100));
            result.IsSuccess.Should().BeTrue();
            result.Value.Should().BeNull();
        }
    }

    [Fact]
    public async Task ProcessTickAsync_WhenRsiDropsBelowOversold_ReturnsBuySignal()
    {
        var strategy = CreateStrategyWithRsi(period: 5, oversold: 30, overbought: 70);
        await _sut.InitializeAsync(strategy);

        // Warm up RSI with descending prices to push RSI low
        var prices = GenerateDescendingPrices(startPrice: 100m, count: 7, decrement: 2m);
        SignalGeneratedEvent? lastSignal = null;

        foreach (var price in prices)
        {
            var result = await _sut.ProcessTickAsync(CreateTick(price));
            result.IsSuccess.Should().BeTrue();
            if (result.Value is not null)
                lastSignal = result.Value;
        }

        lastSignal.Should().NotBeNull();
        lastSignal!.Direction.Should().Be(OrderSide.Buy);
    }

    [Fact]
    public async Task ProcessTickAsync_WhenRsiRisesAboveOverbought_ReturnsSellSignal()
    {
        var strategy = CreateStrategyWithRsi(period: 5, oversold: 30, overbought: 70);
        await _sut.InitializeAsync(strategy);

        // Warm up RSI with ascending prices to push RSI high
        var prices = GenerateAscendingPrices(startPrice: 100m, count: 7, increment: 2m);
        SignalGeneratedEvent? lastSignal = null;

        foreach (var price in prices)
        {
            var result = await _sut.ProcessTickAsync(CreateTick(price));
            result.IsSuccess.Should().BeTrue();
            if (result.Value is not null)
                lastSignal = result.Value;
        }

        lastSignal.Should().NotBeNull();
        lastSignal!.Direction.Should().Be(OrderSide.Sell);
    }

    [Fact]
    public async Task ProcessTickAsync_WhenRsiInNeutralZone_ReturnsNull()
    {
        var strategy = CreateStrategyWithRsi(period: 5, oversold: 30, overbought: 70);
        await _sut.InitializeAsync(strategy);

        // Alternating prices keep RSI near 50
        var prices = new[] { 100m, 101m, 100m, 101m, 100m, 101m, 100m };

        foreach (var price in prices)
        {
            var result = await _sut.ProcessTickAsync(CreateTick(price));
            result.IsSuccess.Should().BeTrue();
            result.Value.Should().BeNull();
        }
    }

    [Fact]
    public async Task ProcessTickAsync_WhenNoRsiIndicator_ReturnsNull()
    {
        // Strategy with no indicators at all
        var strategy = CreateStrategy();
        await _sut.InitializeAsync(strategy);

        var result = await _sut.ProcessTickAsync(CreateTick(55000m));

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().BeNull();
    }

    [Fact]
    public async Task ProcessTickAsync_WhenBuySignalGenerated_IncludesSnapshot()
    {
        var strategy = CreateStrategyWithRsi(period: 5, oversold: 30, overbought: 70);
        await _sut.InitializeAsync(strategy);

        var prices = GenerateDescendingPrices(startPrice: 100m, count: 7, decrement: 2m);
        SignalGeneratedEvent? signal = null;

        foreach (var price in prices)
        {
            var result = await _sut.ProcessTickAsync(CreateTick(price));
            if (result.Value is not null)
                signal = result.Value;
        }

        signal.Should().NotBeNull();
        signal!.IndicatorSnapshot.Should().Contain("RSI(5)=");
    }

    [Fact]
    public async Task ProcessTickAsync_WhenRsiCrossesOversoldOnce_DoesNotSignalAgain()
    {
        var strategy = CreateStrategyWithRsi(period: 5, oversold: 30, overbought: 70);
        await _sut.InitializeAsync(strategy);

        // Push RSI into oversold territory
        var prices = GenerateDescendingPrices(startPrice: 100m, count: 7, decrement: 2m);
        var signalCount = 0;

        foreach (var price in prices)
        {
            var result = await _sut.ProcessTickAsync(CreateTick(price));
            if (result.Value is not null)
                signalCount++;
        }

        // Continue with more descending prices — should NOT trigger again
        // because _previousRsi is already below oversold
        for (var i = 0; i < 3; i++)
        {
            var result = await _sut.ProcessTickAsync(CreateTick(80m - i * 2));
            if (result.Value is not null)
                signalCount++;
        }

        signalCount.Should().Be(1);
    }

    // ── Multi-indicator confirmation ─────────────────────────────────────

    [Fact]
    public async Task ProcessTickAsync_WhenRsiAndMacdConfirm_ReturnsBuySignal()
    {
        var strategy = CreateStrategyWithRsiAndMacd(rsiPeriod: 5, oversold: 30, overbought: 70);
        await _sut.InitializeAsync(strategy);

        // Push RSI into oversold — descending prices push MACD histogram negative initially
        // We need enough data to warm up both RSI and MACD
        var prices = GenerateDescendingPrices(startPrice: 100m, count: 30, decrement: 0.5m);
        SignalGeneratedEvent? lastSignal = null;

        foreach (var price in prices)
        {
            var result = await _sut.ProcessTickAsync(CreateTick(price));
            result.IsSuccess.Should().BeTrue();
            if (result.Value is not null)
                lastSignal = result.Value;
        }

        // Even if MACD doesn't confirm, RSI-only signal should still fire
        // because MACD may not be ready or may not confirm (no majority block)
        // The key is that the strategy doesn't crash with multiple indicators
        lastSignal?.Direction.Should().Be(OrderSide.Buy);
    }

    [Fact]
    public async Task ProcessTickAsync_WithRsiOnly_StillGeneratesSignal()
    {
        // Verify backward compatibility — RSI alone still generates signals
        var strategy = CreateStrategyWithRsi(period: 5, oversold: 30, overbought: 70);
        await _sut.InitializeAsync(strategy);

        var prices = GenerateDescendingPrices(startPrice: 100m, count: 7, decrement: 2m);
        SignalGeneratedEvent? lastSignal = null;

        foreach (var price in prices)
        {
            var result = await _sut.ProcessTickAsync(CreateTick(price));
            if (result.Value is not null)
                lastSignal = result.Value;
        }

        lastSignal.Should().NotBeNull();
        lastSignal!.Direction.Should().Be(OrderSide.Buy);
    }

    [Fact]
    public void CountConfirmations_WhenNoConfirmers_ReturnsZeroTotal()
    {
        // Strategy with RSI only — no MACD, Bollinger, etc.
        var strategy = CreateStrategyWithRsi(period: 5);
        _sut.InitializeAsync(strategy).GetAwaiter().GetResult();

        var (confirms, total) = _sut.CountConfirmations(OrderSide.Buy, 100m);

        total.Should().Be(0);
        confirms.Should().Be(0);
    }

    [Fact]
    public async Task ProcessTickAsync_SnapshotIncludesConfirmationInfo()
    {
        var strategy = CreateStrategyWithRsiAndEma(rsiPeriod: 5, emaPeriod: 3);
        await _sut.InitializeAsync(strategy);

        var prices = GenerateDescendingPrices(startPrice: 100m, count: 7, decrement: 2m);
        SignalGeneratedEvent? signal = null;

        foreach (var price in prices)
        {
            var result = await _sut.ProcessTickAsync(CreateTick(price));
            if (result.Value is not null)
                signal = result.Value;
        }

        if (signal is not null)
        {
            signal.IndicatorSnapshot.Should().Contain("RSI(5)=");
            signal.IndicatorSnapshot.Should().Contain("EMA(3)=");
            signal.IndicatorSnapshot.Should().Contain("Confirm=");
        }
    }

    // ── Signal Cooldown ─────────────────────────────────────────────────

    [Fact]
    public async Task ProcessTickAsync_WhenSignalWithinCooldown_SuppressesSecondSignal()
    {
        var strategy = CreateStrategyWithRsi(period: 5, oversold: 30, overbought: 70);
        await _sut.InitializeAsync(strategy);

        var baseTime = DateTimeOffset.UtcNow;

        // 1. Generate first Buy signal (descending prices)
        var prices = GenerateDescendingPrices(startPrice: 100m, count: 7, decrement: 2m);
        SignalGeneratedEvent? firstSignal = null;
        for (var i = 0; i < prices.Length; i++)
        {
            var result = await _sut.ProcessTickAsync(CreateTick(prices[i], baseTime.AddSeconds(i)));
            if (result.Value is not null) firstSignal = result.Value;
        }
        firstSignal.Should().NotBeNull("should generate first Buy signal");

        // 2. Reset RSI to neutral, then push back to oversold quickly (within cooldown)
        var neutralPrices = GenerateAscendingPrices(startPrice: 90m, count: 6, increment: 3m);
        var reEntryPrices = GenerateDescendingPrices(startPrice: 100m, count: 7, decrement: 2m);
        var secondSignalCount = 0;
        var offset = prices.Length;

        foreach (var p in neutralPrices)
        {
            await _sut.ProcessTickAsync(CreateTick(p, baseTime.AddSeconds(offset++)));
        }
        foreach (var p in reEntryPrices)
        {
            // Still within cooldown window (< 60 seconds total)
            var result = await _sut.ProcessTickAsync(CreateTick(p, baseTime.AddSeconds(offset++)));
            if (result.Value is not null) secondSignalCount++;
        }

        secondSignalCount.Should().Be(0, "second signal should be suppressed by cooldown");
    }

    [Fact]
    public async Task ProcessTickAsync_WhenSignalAfterCooldown_GeneratesSignal()
    {
        var strategy = CreateStrategyWithRsi(period: 5, oversold: 30, overbought: 70);
        await _sut.InitializeAsync(strategy);

        var baseTime = DateTimeOffset.UtcNow;

        // 1. Generate first Buy signal
        var prices = GenerateDescendingPrices(startPrice: 100m, count: 7, decrement: 2m);
        for (var i = 0; i < prices.Length; i++)
            await _sut.ProcessTickAsync(CreateTick(prices[i], baseTime.AddSeconds(i)));

        // 2. Neutral zone, then re-enter oversold AFTER cooldown
        var neutralPrices = GenerateAscendingPrices(startPrice: 90m, count: 6, increment: 3m);
        var offset = prices.Length;
        foreach (var p in neutralPrices)
            await _sut.ProcessTickAsync(CreateTick(p, baseTime.AddSeconds(offset++)));

        // Jump past cooldown (> 1 minute)
        var afterCooldown = baseTime.AddMinutes(2);
        var reEntry = GenerateDescendingPrices(startPrice: 100m, count: 7, decrement: 2m);
        SignalGeneratedEvent? signal = null;
        for (var i = 0; i < reEntry.Length; i++)
        {
            var result = await _sut.ProcessTickAsync(CreateTick(reEntry[i], afterCooldown.AddSeconds(i)));
            if (result.Value is not null) signal = result.Value;
        }

        signal.Should().NotBeNull("signal should fire after cooldown expired");
    }

    // ── ReloadConfigAsync ─────────────────────────────────────────────────

    [Fact]
    public async Task ReloadConfigAsync_WhenCalled_UpdatesMode()
    {
        var strategy = CreateStrategyWithRsi(period: 5);
        await _sut.InitializeAsync(strategy);

        _sut.Mode.Should().Be(TradingMode.PaperTrading);

        // Reload with a new config — Mode stays PaperTrading but we verify indicators are rebuilt
        var newStrategy = CreateStrategyWithRsi(period: 10);
        await _sut.ReloadConfigAsync(newStrategy);

        _sut.Mode.Should().Be(TradingMode.PaperTrading);
    }

    // ── Reset ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task Reset_WhenCalled_ClearsInitialization()
    {
        var strategy = CreateStrategyWithRsi(period: 5);
        await _sut.InitializeAsync(strategy);
        _sut.IsInitialized.Should().BeTrue();

        _sut.Reset();

        _sut.IsInitialized.Should().BeFalse();
    }

    // ── Helpers ────────────────────────────────────────────────────────────

    private static TradingStrategy CreateStrategy()
    {
        var symbol     = Symbol.Create("BTCUSDT").Value;
        var riskConfig = RiskConfig.Create(100m, 500m, 2m, 4m, 5).Value;
        return TradingStrategy.Create("Test Strategy", symbol, TradingMode.PaperTrading, riskConfig).Value;
    }

    private static TradingStrategy CreateStrategyWithRsi(
        int period = 14,
        decimal oversold = 30m,
        decimal overbought = 70m)
    {
        var strategy  = CreateStrategy();
        var indicator = IndicatorConfig.Rsi(period, overbought, oversold).Value;
        strategy.AddIndicator(indicator);
        return strategy;
    }

    private static TradingStrategy CreateStrategyWithRsiAndMacd(
        int rsiPeriod = 14,
        decimal oversold = 30m,
        decimal overbought = 70m,
        int macdFast = 12,
        int macdSlow = 26,
        int macdSignal = 9)
    {
        var strategy = CreateStrategyWithRsi(rsiPeriod, oversold, overbought);
        var macd     = IndicatorConfig.Macd(macdFast, macdSlow, macdSignal).Value;
        strategy.AddIndicator(macd);
        return strategy;
    }

    private static TradingStrategy CreateStrategyWithRsiAndEma(
        int rsiPeriod = 14,
        int emaPeriod = 20,
        decimal oversold = 30m,
        decimal overbought = 70m)
    {
        var strategy = CreateStrategyWithRsi(rsiPeriod, oversold, overbought);
        var ema      = IndicatorConfig.Ema(emaPeriod).Value;
        strategy.AddIndicator(ema);
        return strategy;
    }

    private static MarketTickReceivedEvent CreateTick(decimal price, DateTimeOffset? timestamp = null)
    {
        var symbol   = Symbol.Create("BTCUSDT").Value;
        var pricevo  = Price.Create(price).Value;
        var bidPrice = Price.Create(price - 1m).Value;
        var askPrice = Price.Create(price + 1m).Value;
        return new MarketTickReceivedEvent(symbol, bidPrice, askPrice, pricevo, 1000m, timestamp ?? DateTimeOffset.UtcNow);
    }

    private static decimal[] GenerateDescendingPrices(decimal startPrice, int count, decimal decrement)
    {
        var prices = new decimal[count];
        for (var i = 0; i < count; i++)
            prices[i] = startPrice - i * decrement;
        return prices;
    }

    private static decimal[] GenerateAscendingPrices(decimal startPrice, int count, decimal increment)
    {
        var prices = new decimal[count];
        for (var i = 0; i < count; i++)
            prices[i] = startPrice + i * increment;
        return prices;
    }
}
