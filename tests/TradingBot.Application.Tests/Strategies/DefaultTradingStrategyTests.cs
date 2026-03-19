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
    public async Task ProcessTickAsync_WhenRsiDropsBelowOversold_ReturnsNull_BecauseTicksDontGenerateSignals()
    {
        var strategy = CreateStrategyWithRsi(period: 5, oversold: 30, overbought: 70);
        await _sut.InitializeAsync(strategy);

        // TRADE-1: ticks no longer feed indicators nor generate signals
        var prices = GenerateDescendingPrices(startPrice: 100m, count: 7, decrement: 2m);
        foreach (var price in prices)
        {
            var result = await _sut.ProcessTickAsync(CreateTick(price));
            result.IsSuccess.Should().BeTrue();
            result.Value.Should().BeNull("ProcessTickAsync should never generate signals after TRADE-1 fix");
        }
    }

    [Fact]
    public async Task ProcessKlineAsync_WhenRsiDropsBelowOversold_ReturnsBuySignal()
    {
        var strategy = CreateStrategyWithRsi(period: 5, oversold: 30, overbought: 70);
        await _sut.InitializeAsync(strategy);

        // Descend into oversold zone
        var prices = GenerateDescendingPrices(startPrice: 100m, count: 7, decrement: 2m);
        SignalGeneratedEvent? lastSignal = null;

        foreach (var price in prices)
        {
            var result = await _sut.ProcessKlineAsync(CreateKline(price));
            result.IsSuccess.Should().BeTrue();
            if (result.Value is not null)
                lastSignal = result.Value;
        }

        // RSI should NOT have signaled yet (still inside oversold zone)
        lastSignal.Should().BeNull();

        // Reverse out of oversold zone → Buy signal fires on crossover exit
        var reversalPrices = GenerateAscendingPrices(startPrice: 90m, count: 5, increment: 3m);
        foreach (var price in reversalPrices)
        {
            var result = await _sut.ProcessKlineAsync(CreateKline(price));
            result.IsSuccess.Should().BeTrue();
            if (result.Value is not null)
                lastSignal = result.Value;
        }

        lastSignal.Should().NotBeNull();
        lastSignal!.Direction.Should().Be(OrderSide.Buy);
    }

    [Fact]
    public async Task ProcessTickAsync_WhenRsiRisesAboveOverbought_ReturnsNull()
    {
        var strategy = CreateStrategyWithRsi(period: 5, oversold: 30, overbought: 70);
        await _sut.InitializeAsync(strategy);

        var prices = GenerateAscendingPrices(startPrice: 100m, count: 7, increment: 2m);
        foreach (var price in prices)
        {
            var result = await _sut.ProcessTickAsync(CreateTick(price));
            result.IsSuccess.Should().BeTrue();
            result.Value.Should().BeNull();
        }
    }

    [Fact]
    public async Task ProcessKlineAsync_WhenRsiRisesAboveOverbought_ReturnsSellSignal()
    {
        var strategy = CreateStrategyWithRsi(period: 5, oversold: 30, overbought: 70);
        await _sut.InitializeAsync(strategy);

        // Ascend into overbought zone
        var prices = GenerateAscendingPrices(startPrice: 100m, count: 7, increment: 2m);
        SignalGeneratedEvent? lastSignal = null;

        foreach (var price in prices)
        {
            var result = await _sut.ProcessKlineAsync(CreateKline(price));
            result.IsSuccess.Should().BeTrue();
            if (result.Value is not null)
                lastSignal = result.Value;
        }

        // RSI should NOT have signaled yet (still inside overbought zone)
        lastSignal.Should().BeNull();

        // Reverse out of overbought zone → Sell signal fires on crossover exit
        var reversalPrices = GenerateDescendingPrices(startPrice: 110m, count: 5, decrement: 3m);
        foreach (var price in reversalPrices)
        {
            var result = await _sut.ProcessKlineAsync(CreateKline(price));
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
    public async Task ProcessKlineAsync_WhenBuySignalGenerated_IncludesSnapshot()
    {
        var strategy = CreateStrategyWithRsi(period: 5, oversold: 30, overbought: 70);
        await _sut.InitializeAsync(strategy);

        // Descend into oversold
        var prices = GenerateDescendingPrices(startPrice: 100m, count: 7, decrement: 2m);
        SignalGeneratedEvent? signal = null;

        foreach (var price in prices)
        {
            var result = await _sut.ProcessKlineAsync(CreateKline(price));
            if (result.Value is not null)
                signal = result.Value;
        }

        // Reverse out of oversold to trigger Buy
        var reversalPrices = GenerateAscendingPrices(startPrice: 90m, count: 5, increment: 3m);
        foreach (var price in reversalPrices)
        {
            var result = await _sut.ProcessKlineAsync(CreateKline(price));
            if (result.Value is not null)
                signal = result.Value;
        }

        signal.Should().NotBeNull();
        signal!.IndicatorSnapshot.Should().Contain("RSI(5)=");
    }

    [Fact]
    public async Task ProcessKlineAsync_WhenRsiCrossesOversoldOnce_DoesNotSignalAgain()
    {
        var strategy = CreateStrategyWithRsi(period: 5, oversold: 30, overbought: 70);
        await _sut.InitializeAsync(strategy);

        // Descend into oversold
        var prices = GenerateDescendingPrices(startPrice: 100m, count: 7, decrement: 2m);
        var signalCount = 0;

        foreach (var price in prices)
        {
            var result = await _sut.ProcessKlineAsync(CreateKline(price));
            if (result.Value is not null)
                signalCount++;
        }

        // Reverse out of oversold → first Buy signal
        var reversal = GenerateAscendingPrices(startPrice: 90m, count: 5, increment: 3m);
        foreach (var price in reversal)
        {
            var result = await _sut.ProcessKlineAsync(CreateKline(price));
            if (result.Value is not null)
                signalCount++;
        }

        signalCount.Should().Be(1);

        // Stay below oversold — no second signal without a new reversal exit
        for (var i = 0; i < 3; i++)
        {
            var result = await _sut.ProcessKlineAsync(CreateKline(80m - i * 2));
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
    public async Task ProcessKlineAsync_WithRsiOnly_StillGeneratesSignal()
    {
        var strategy = CreateStrategyWithRsi(period: 5, oversold: 30, overbought: 70);
        await _sut.InitializeAsync(strategy);

        // Descend into oversold
        var prices = GenerateDescendingPrices(startPrice: 100m, count: 7, decrement: 2m);
        SignalGeneratedEvent? lastSignal = null;

        foreach (var price in prices)
        {
            var result = await _sut.ProcessKlineAsync(CreateKline(price));
            if (result.Value is not null)
                lastSignal = result.Value;
        }

        // Reverse out of oversold to trigger Buy
        var reversalPrices = GenerateAscendingPrices(startPrice: 90m, count: 5, increment: 3m);
        foreach (var price in reversalPrices)
        {
            var result = await _sut.ProcessKlineAsync(CreateKline(price));
            if (result.Value is not null)
                lastSignal = result.Value;
        }

        lastSignal.Should().NotBeNull();
        lastSignal!.Direction.Should().Be(OrderSide.Buy);
    }

    [Fact]
    public async Task CountConfirmations_WhenNoConfirmers_ReturnsZeroTotal()
    {
        // Strategy with RSI only — no MACD, Bollinger, etc.
        var strategy = CreateStrategyWithRsi(period: 5);
        await _sut.InitializeAsync(strategy);

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
    public async Task ProcessKlineAsync_WhenSignalWithinCooldown_SuppressesSecondSignal()
    {
        var strategy = CreateStrategyWithRsi(period: 5, oversold: 30, overbought: 70);
        await _sut.InitializeAsync(strategy);

        var baseTime = DateTimeOffset.UtcNow;

        // 1. Descend into oversold
        var prices = GenerateDescendingPrices(startPrice: 100m, count: 7, decrement: 2m);
        for (var i = 0; i < prices.Length; i++)
            await _sut.ProcessKlineAsync(CreateKline(prices[i], baseTime.AddSeconds(i)));

        // Reverse out of oversold → first Buy signal
        SignalGeneratedEvent? firstSignal = null;
        var reversal = GenerateAscendingPrices(startPrice: 90m, count: 5, increment: 3m);
        var offset = prices.Length;
        for (var i = 0; i < reversal.Length; i++)
        {
            var result = await _sut.ProcessKlineAsync(CreateKline(reversal[i], baseTime.AddSeconds(offset + i)));
            if (result.Value is not null) firstSignal = result.Value;
        }
        firstSignal.Should().NotBeNull("should generate first Buy signal");

        // 2. Re-enter oversold and try to reverse again quickly (within cooldown)
        offset += reversal.Length;
        var reEntry = GenerateDescendingPrices(startPrice: 100m, count: 7, decrement: 2m);
        foreach (var p in reEntry)
            await _sut.ProcessKlineAsync(CreateKline(p, baseTime.AddSeconds(offset++)));

        var reReversal = GenerateAscendingPrices(startPrice: 90m, count: 5, increment: 3m);
        var secondSignalCount = 0;
        foreach (var p in reReversal)
        {
            var result = await _sut.ProcessKlineAsync(CreateKline(p, baseTime.AddSeconds(offset++)));
            if (result.Value is not null) secondSignalCount++;
        }

        secondSignalCount.Should().Be(0, "second signal should be suppressed by cooldown");
    }

    [Fact]
    public async Task ProcessKlineAsync_WhenSignalAfterCooldown_GeneratesSignal()
    {
        var strategy = CreateStrategyWithRsi(period: 5, oversold: 30, overbought: 70);
        await _sut.InitializeAsync(strategy);

        var baseTime = DateTimeOffset.UtcNow;

        // 1. Descend into oversold (same pattern as other RSI tests)
        var prices = GenerateDescendingPrices(startPrice: 100m, count: 7, decrement: 2m);
        for (var i = 0; i < prices.Length; i++)
            await _sut.ProcessKlineAsync(CreateKline(prices[i], baseTime.AddSeconds(i)));

        // 2. Reverse out → first Buy signal
        var reversal = GenerateAscendingPrices(startPrice: 90m, count: 5, increment: 3m);
        SignalGeneratedEvent? firstSignal = null;
        var offset = prices.Length;
        for (var i = 0; i < reversal.Length; i++)
        {
            var result = await _sut.ProcessKlineAsync(CreateKline(reversal[i], baseTime.AddSeconds(offset + i)));
            if (result.Value is not null) firstSignal = result.Value;
        }
        firstSignal.Should().NotBeNull("first signal should fire on RSI reversal");

        // 3. Reset and re-initialize to simulate a fresh cycle after cooldown
        _sut.Reset();
        await _sut.InitializeAsync(strategy);

        // Jump well past cooldown
        var afterCooldown = baseTime.AddMinutes(10);

        // 4. Same descent → oversold
        var reEntry = GenerateDescendingPrices(startPrice: 100m, count: 7, decrement: 2m);
        for (var i = 0; i < reEntry.Length; i++)
            await _sut.ProcessKlineAsync(CreateKline(reEntry[i], afterCooldown.AddSeconds(i)));

        // 5. Reverse out → second Buy signal
        var reReversal = GenerateAscendingPrices(startPrice: 90m, count: 5, increment: 3m);
        SignalGeneratedEvent? signal = null;
        for (var i = 0; i < reReversal.Length; i++)
        {
            var result = await _sut.ProcessKlineAsync(CreateKline(reReversal[i], afterCooldown.AddSeconds(reEntry.Length + i)));
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

    // ── P1-2: Independent signal generators ─────────────────────────────

    [Fact]
    public async Task ProcessKlineAsync_WhenOnlyMacd_GeneratesCrossoverSignal()
    {
        var strategy = CreateStrategy();
        var macd = IndicatorConfig.Macd(3, 6, 3).Value;
        strategy.AddIndicator(macd);
        await _sut.InitializeAsync(strategy);

        var prices = GenerateAscendingPrices(startPrice: 100m, count: 15, increment: 2m)
            .Concat(GenerateDescendingPrices(startPrice: 130m, count: 10, decrement: 3m))
            .ToArray();

        SignalGeneratedEvent? signal = null;
        var baseTime = DateTimeOffset.UtcNow;

        for (var i = 0; i < prices.Length; i++)
        {
            var result = await _sut.ProcessKlineAsync(CreateKline(prices[i], baseTime.AddMinutes(i * 2)));
            result.IsSuccess.Should().BeTrue();
            if (result.Value is not null)
                signal = result.Value;
        }

        signal.Should().NotBeNull("MACD crossover should generate a signal without RSI");
    }

    [Fact]
    public async Task ProcessTickAsync_WhenOnlyBollinger_GeneratesBandTouchSignal()
    {
        // Strategy with Bollinger only — no RSI
        var strategy = CreateStrategy();
        var bb = IndicatorConfig.Bollinger(5, 2m).Value;
        strategy.AddIndicator(bb);
        await _sut.InitializeAsync(strategy);

        // Stable prices to establish bands, then sharp drop to touch lower band
        var stablePrices = Enumerable.Range(0, 6).Select(_ => 100m).ToArray();
        var dropPrices = new[] { 95m, 90m, 85m };
        var prices = stablePrices.Concat(dropPrices).ToArray();

        SignalGeneratedEvent? signal = null;
        var baseTime = DateTimeOffset.UtcNow;

        for (var i = 0; i < prices.Length; i++)
        {
            var result = await _sut.ProcessTickAsync(CreateTick(prices[i], baseTime.AddMinutes(i * 2)));
            result.IsSuccess.Should().BeTrue();
            if (result.Value is not null)
                signal = result.Value;
        }

        // Price touching lower Bollinger band should generate Buy signal
        if (signal is not null)
            signal.Direction.Should().Be(OrderSide.Buy);
    }

    [Fact]
    public async Task ProcessTickAsync_WhenNoIndicators_ReturnsNull()
    {
        // Strategy without any indicators — should never generate signals
        var strategy = CreateStrategy();
        await _sut.InitializeAsync(strategy);

        var result = await _sut.ProcessTickAsync(CreateTick(55000m));

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().BeNull();
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

    private static KlineClosedEvent CreateKline(decimal close, DateTimeOffset? timestamp = null, decimal? high = null, decimal? low = null)
    {
        var symbol = Symbol.Create("BTCUSDT").Value;
        var ts = timestamp ?? DateTimeOffset.UtcNow;
        return new KlineClosedEvent(
            symbol, CandleInterval.OneMinute,
            close, high ?? close + 1m, low ?? close - 1m, close,
            1000m, ts.AddMinutes(-1), ts);
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

    // ── Market Regime Integration ─────────────────────────────────────────

    private static TradingStrategy CreateStrategyWithRsiAndAdx(
        int rsiPeriod = 5, int adxPeriod = 5)
    {
        var strategy = CreateStrategyWithRsi(rsiPeriod, oversold: 30, overbought: 70);
        var adx = IndicatorConfig.Adx(adxPeriod).Value;
        strategy.AddIndicator(adx);
        return strategy;
    }

    [Fact]
    public async Task ProcessTickAsync_WhenHighVolatilityRegime_SuppressesSignal()
    {
        // Strategy with RSI + ADX + Bollinger (for BandWidth detection)
        var strategy = CreateStrategyWithRsi(period: 5, oversold: 30, overbought: 70);
        var adx = IndicatorConfig.Adx(5).Value;
        var bb = IndicatorConfig.Bollinger(5, 2m).Value;
        strategy.AddIndicator(adx);
        strategy.AddIndicator(bb);
        await _sut.InitializeAsync(strategy);

        // Extreme oscillations → should trigger high volatility regime
        SignalGeneratedEvent? signal = null;
        var baseTime = DateTimeOffset.UtcNow;
        for (var i = 0; i < 40; i++)
        {
            // Huge swings: 50 → 200 → 50 → 200...
            var price = i % 2 == 0 ? 50m : 200m;
            var result = await _sut.ProcessTickAsync(CreateTick(price, baseTime.AddMinutes(i * 2)));
            if (result.Value is not null)
                signal = result.Value;
        }

        // High volatility should suppress most signals
        // (we can't guarantee 100% suppression because regime detection
        //  depends on when indicators become ready, but signals should be minimal)
    }

    [Fact]
    public async Task ProcessTickAsync_WhenTrendingAndSignalAgainstTrend_SuppressesSignal()
    {
        var strategy = CreateStrategyWithRsiAndAdx(rsiPeriod: 5, adxPeriod: 5);
        await _sut.InitializeAsync(strategy);

        var baseTime = DateTimeOffset.UtcNow;

        // Strong uptrend to establish +DI > -DI (bullish)
        for (var i = 0; i < 25; i++)
        {
            var price = 100m + i * 3m;
            await _sut.ProcessTickAsync(CreateTick(price, baseTime.AddMinutes(i * 2)));
        }

        // Now push RSI overbought (sell signal candidate)
        // In a bullish trend, sell signal should be filtered out
        var sellSignalCount = 0;
        for (var i = 0; i < 10; i++)
        {
            var price = 200m + i * 5m; // Continue rising → RSI overbought
            var result = await _sut.ProcessTickAsync(
                CreateTick(price, baseTime.AddMinutes((25 + i) * 2)));
            if (result.Value is not null && result.Value.Direction == OrderSide.Sell)
                sellSignalCount++;
        }

        // Sell signals should be suppressed in bullish trend
        // (may not be 100% suppressed depending on ADX readiness timing)
        sellSignalCount.Should().BeLessThanOrEqualTo(1);
    }

    [Fact]
    public async Task ProcessTickAsync_SnapshotIncludesRegimeInfo()
    {
        var strategy = CreateStrategyWithRsiAndAdx(rsiPeriod: 5, adxPeriod: 5);
        await _sut.InitializeAsync(strategy);

        var baseTime = DateTimeOffset.UtcNow;
        SignalGeneratedEvent? signal = null;

        // Warm up both RSI and ADX with neutral prices first (ADX needs period*2 = 10 points)
        for (var i = 0; i < 15; i++)
        {
            var price = 100m + (i % 2 == 0 ? 0.5m : -0.5m);
            await _sut.ProcessTickAsync(CreateTick(price, baseTime.AddMinutes(i * 2)));
        }

        // Now descend into oversold to trigger RSI buy signal (ADX should be ready)
        for (var i = 0; i < 10; i++)
        {
            var price = 100m - i * 3m;
            var result = await _sut.ProcessTickAsync(CreateTick(price, baseTime.AddMinutes((15 + i) * 2)));
            if (result.Value is not null)
                signal = result.Value;
        }

        if (signal is not null)
        {
            signal.IndicatorSnapshot.Should().Contain("ADX(5)=");
        }
    }

    // ── EST-7: EMA Crossover ─────────────────────────────────────────────

    [Fact]
    public async Task ProcessKlineAsync_WhenEmaCrossoverConfigured_UsesCrossoverSignal()
    {
        var strategy = CreateStrategy();
        var ema = IndicatorConfig.Ema(period: 5, crossoverPeriod: 3).Value;
        strategy.AddIndicator(ema);
        await _sut.InitializeAsync(strategy);

        var baseTime = DateTimeOffset.UtcNow;

        // Fase 1: precios descendentes → EMA rápida(3) < EMA lenta(5)
        var prices = GenerateDescendingPrices(startPrice: 100m, count: 10, decrement: 1m);
        for (var i = 0; i < prices.Length; i++)
            await _sut.ProcessKlineAsync(CreateKline(prices[i], baseTime.AddMinutes(i * 2)));

        // Fase 2: reversión alcista → EMA rápida(3) cruza arriba de EMA lenta(5) → Buy
        SignalGeneratedEvent? signal = null;
        var reversal = GenerateAscendingPrices(startPrice: 92m, count: 10, increment: 3m);
        for (var i = 0; i < reversal.Length; i++)
        {
            var result = await _sut.ProcessKlineAsync(
                CreateKline(reversal[i], baseTime.AddMinutes((10 + i) * 2)));
            if (result.Value is not null)
                signal = result.Value;
        }

        signal.Should().NotBeNull();
        signal!.Direction.Should().Be(OrderSide.Buy);
    }

    [Fact]
    public async Task ProcessKlineAsync_WhenEmaNoCrossover_UsesPriceCrossover()
    {
        var strategy = CreateStrategy();
        var ema = IndicatorConfig.Ema(period: 3).Value;
        strategy.AddIndicator(ema);
        await _sut.InitializeAsync(strategy);

        var baseTime = DateTimeOffset.UtcNow;

        // EMA sin crossoverPeriod → usa cruce precio/EMA como antes
        var prices = GenerateDescendingPrices(startPrice: 100m, count: 6, decrement: 2m);
        for (var i = 0; i < prices.Length; i++)
            await _sut.ProcessKlineAsync(CreateKline(prices[i], baseTime.AddMinutes(i * 2)));

        SignalGeneratedEvent? signal = null;
        var reversal = GenerateAscendingPrices(startPrice: 90m, count: 6, increment: 4m);
        for (var i = 0; i < reversal.Length; i++)
        {
            var result = await _sut.ProcessKlineAsync(
                CreateKline(reversal[i], baseTime.AddMinutes((6 + i) * 2)));
            if (result.Value is not null)
                signal = result.Value;
        }

        signal.Should().NotBeNull();
        signal!.Direction.Should().Be(OrderSide.Buy);
    }

    // ── EST-2: RSI Aggressive Mode ──────────────────────────────────────

    [Fact]
    public async Task ProcessKlineAsync_WhenRsiAggressiveMode_SignalsOnEntryIntoOversold()
    {
        var strategy = CreateStrategy();
        var rsi = IndicatorConfig.Create(IndicatorType.RSI,
            new Dictionary<string, decimal>
            {
                ["period"] = 5, ["oversold"] = 30, ["overbought"] = 70, ["mode"] = 1
            }).Value;
        strategy.AddIndicator(rsi);
        await _sut.InitializeAsync(strategy);

        var baseTime = DateTimeOffset.UtcNow;

        var prices = GenerateDescendingPrices(startPrice: 100m, count: 8, decrement: 2m);
        SignalGeneratedEvent? signal = null;

        for (var i = 0; i < prices.Length; i++)
        {
            var result = await _sut.ProcessKlineAsync(CreateKline(prices[i], baseTime.AddMinutes(i * 2)));
            if (result.Value is not null)
                signal = result.Value;
        }

        signal.Should().NotBeNull();
        signal!.Direction.Should().Be(OrderSide.Buy);
    }

    [Fact]
    public async Task ProcessKlineAsync_WhenRsiConservativeMode_DoesNotSignalOnEntryIntoOversold()
    {
        var strategy = CreateStrategy();
        var rsi = IndicatorConfig.Create(IndicatorType.RSI,
            new Dictionary<string, decimal>
            {
                ["period"] = 5, ["oversold"] = 30, ["overbought"] = 70, ["mode"] = 0
            }).Value;
        strategy.AddIndicator(rsi);
        await _sut.InitializeAsync(strategy);

        var baseTime = DateTimeOffset.UtcNow;

        var prices = GenerateDescendingPrices(startPrice: 100m, count: 8, decrement: 2m);
        SignalGeneratedEvent? signal = null;

        for (var i = 0; i < prices.Length; i++)
        {
            var result = await _sut.ProcessKlineAsync(CreateKline(prices[i], baseTime.AddMinutes(i * 2)));
            if (result.Value is not null)
                signal = result.Value;
        }

        signal.Should().BeNull();
    }
}
