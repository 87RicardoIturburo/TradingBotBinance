using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using TradingBot.Application.Backtesting;
using TradingBot.Core.Common;
using TradingBot.Core.Entities;
using TradingBot.Core.Enums;
using TradingBot.Core.Events;
using TradingBot.Core.Interfaces.Services;
using TradingBot.Core.Interfaces.Trading;
using TradingBot.Core.ValueObjects;

namespace TradingBot.Application.Tests.Backtesting;

public sealed class BacktestEngineTests
{
    private readonly BacktestEngine _engine = new(NullLogger<BacktestEngine>.Instance);
    private readonly ITradingStrategy _strategy = Substitute.For<ITradingStrategy>();
    private readonly IRuleEngine _ruleEngine = Substitute.For<IRuleEngine>();

    private static TradingStrategy CreateStrategy()
    {
        var symbol = Symbol.Create("BTCUSDT").Value;
        var risk = RiskConfig.Create(100, 500, 5, 5, 5).Value;
        var strategy = TradingStrategy.Create("Test", symbol, TradingMode.PaperTrading, risk).Value;

        var rsiConfig = IndicatorConfig.Create(IndicatorType.RSI, new Dictionary<string, decimal>
        {
            ["period"] = 14, ["overbought"] = 70, ["oversold"] = 30
        }).Value;
        strategy.AddIndicator(rsiConfig);

        var rule = TradingRule.Create(strategy.Id, "Buy RSI", RuleType.Entry,
            new RuleCondition(ConditionOperator.And,
                [new LeafCondition(IndicatorType.RSI, Comparator.LessThan, 30)]),
            new RuleAction(ActionType.BuyMarket, 50)).Value;
        strategy.AddRule(rule);

        return strategy;
    }

    private static List<Kline> GenerateKlines(int count, decimal startPrice = 50000m)
    {
        var klines = new List<Kline>();
        var baseTime = new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero);

        for (var i = 0; i < count; i++)
        {
            var price = startPrice + (i % 20 - 10) * 100m; // oscila ±1000
            klines.Add(new Kline(
                baseTime.AddMinutes(i), price, price + 50, price - 50, price, 100m));
        }

        return klines;
    }

    [Fact]
    public async Task RunAsync_WhenNoSignals_ReturnsZeroTrades()
    {
        var strategy = CreateStrategy();
        var klines = GenerateKlines(100);

        _strategy.ProcessTickAsync(Arg.Any<MarketTickReceivedEvent>(), Arg.Any<CancellationToken>())
            .Returns(Result<SignalGeneratedEvent?, DomainError>.Success(null));

        var result = await _engine.RunAsync(strategy, _strategy, _ruleEngine, klines);

        result.TotalTrades.Should().Be(0);
        result.TotalPnL.Should().Be(0m);
        result.WinRate.Should().Be(0m);
        result.TotalKlines.Should().Be(100);
        result.Symbol.Should().Be("BTCUSDT");
    }

    [Fact]
    public async Task RunAsync_WhenSignalAndRuleMatch_CreatesTrade()
    {
        var strategy = CreateStrategy();
        var klines = GenerateKlines(50);
        var symbol = Symbol.Create("BTCUSDT").Value;

        // Primera señal en tick 5 (compra), luego nada
        _strategy.ProcessTickAsync(Arg.Any<MarketTickReceivedEvent>(), Arg.Any<CancellationToken>())
            .Returns(Result<SignalGeneratedEvent?, DomainError>.Success(null));

        var buySignal = new SignalGeneratedEvent(
            strategy.Id, symbol, OrderSide.Buy,
            Price.Create(49000m).Value, "RSI(14)=25.0000");

        // Tick 5 genera señal de compra
        _strategy.ProcessTickAsync(
            Arg.Is<MarketTickReceivedEvent>(t => t.Timestamp == klines[5].OpenTime),
            Arg.Any<CancellationToken>())
            .Returns(Result<SignalGeneratedEvent?, DomainError>.Success(buySignal));

        // RuleEngine devuelve orden en la señal de compra
        var orderQty = Quantity.Create(0.001m).Value;
        var orderResult = Order.Create(
            strategy.Id, symbol, OrderSide.Buy, OrderType.Market,
            orderQty, TradingMode.PaperTrading);

        _ruleEngine.EvaluateAsync(strategy, buySignal, Arg.Any<CancellationToken>())
            .Returns(Result<Order?, DomainError>.Success(orderResult.Value));

        // Ninguna regla de salida se activa — la posición se cierra al final del backtest
        _ruleEngine.EvaluateExitRulesAsync(
            Arg.Any<TradingStrategy>(), Arg.Any<Position>(),
            Arg.Any<Price>(), Arg.Any<CancellationToken>())
            .Returns(Result<Order?, DomainError>.Success(null));

        var result = await _engine.RunAsync(strategy, _strategy, _ruleEngine, klines);

        result.TotalTrades.Should().Be(1);
        result.Trades[0].Side.Should().Be(OrderSide.Buy);
        result.Trades[0].ExitReason.Should().Be("Fin del backtest");
    }

    [Fact]
    public async Task RunAsync_WhenStopLossHit_ClosesPosition()
    {
        var strategy = CreateStrategy();
        var symbol = Symbol.Create("BTCUSDT").Value;

        // Crear klines: compra a 50000, luego cae a 47000 (>5% stop-loss)
        var baseTime = new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var klines = new List<Kline>
        {
            new(baseTime, 50000, 50100, 49900, 50000, 100),           // tick 0
            new(baseTime.AddMinutes(1), 50000, 50100, 49900, 50000, 100), // tick 1
            new(baseTime.AddMinutes(2), 49000, 49100, 48900, 49000, 100), // tick 2
            new(baseTime.AddMinutes(3), 47000, 47100, 46900, 47000, 100), // tick 3: -6% → stop-loss
            new(baseTime.AddMinutes(4), 46500, 46600, 46400, 46500, 100), // tick 4
        };

        // Señal de compra en tick 0
        var buySignal = new SignalGeneratedEvent(
            strategy.Id, symbol, OrderSide.Buy,
            Price.Create(50000m).Value, "RSI(14)=25.0000");

        _strategy.ProcessTickAsync(
            Arg.Is<MarketTickReceivedEvent>(t => t.Timestamp == klines[0].OpenTime),
            Arg.Any<CancellationToken>())
            .Returns(Result<SignalGeneratedEvent?, DomainError>.Success(buySignal));

        _strategy.ProcessTickAsync(
            Arg.Is<MarketTickReceivedEvent>(t => t.Timestamp != klines[0].OpenTime),
            Arg.Any<CancellationToken>())
            .Returns(Result<SignalGeneratedEvent?, DomainError>.Success(null));

        var orderQty = Quantity.Create(0.001m).Value;
        var order = Order.Create(
            strategy.Id, symbol, OrderSide.Buy, OrderType.Market,
            orderQty, TradingMode.PaperTrading).Value;

        _ruleEngine.EvaluateAsync(strategy, buySignal, Arg.Any<CancellationToken>())
            .Returns(Result<Order?, DomainError>.Success(order));

        _ruleEngine.EvaluateExitRulesAsync(
            Arg.Any<TradingStrategy>(), Arg.Any<Position>(),
            Arg.Any<Price>(), Arg.Any<CancellationToken>())
            .Returns(Result<Order?, DomainError>.Success(null));

        var result = await _engine.RunAsync(strategy, _strategy, _ruleEngine, klines);

        result.TotalTrades.Should().Be(1);
        result.Trades[0].ExitReason.Should().Be("Stop-loss");
        result.Trades[0].PnL.Should().BeNegative();
    }

    [Fact]
    public async Task RunAsync_EquityCurve_HasExpectedPoints()
    {
        var strategy = CreateStrategy();
        var klines = GenerateKlines(200);

        _strategy.ProcessTickAsync(Arg.Any<MarketTickReceivedEvent>(), Arg.Any<CancellationToken>())
            .Returns(Result<SignalGeneratedEvent?, DomainError>.Success(null));

        var result = await _engine.RunAsync(strategy, _strategy, _ruleEngine, klines);

        // Equity se registra cada 60 velas + la última
        result.EquityCurve.Should().HaveCountGreaterThan(1);
        result.EquityCurve[0].Timestamp.Should().Be(klines[0].OpenTime);
    }

    [Fact]
    public async Task RunAsync_WhenCancelled_ThrowsOperationCancelled()
    {
        var strategy = CreateStrategy();
        var klines = GenerateKlines(1000);

        _strategy.ProcessTickAsync(Arg.Any<MarketTickReceivedEvent>(), Arg.Any<CancellationToken>())
            .Returns(Result<SignalGeneratedEvent?, DomainError>.Success(null));

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var act = () => _engine.RunAsync(strategy, _strategy, _ruleEngine, klines, cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task RunAsync_WhenTakeProfitHit_ClosesWithProfit()
    {
        var strategy = CreateStrategy();
        var symbol = Symbol.Create("BTCUSDT").Value;

        var baseTime = new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var klines = new List<Kline>
        {
            new(baseTime, 50000, 50100, 49900, 50000, 100),           // tick 0: compra
            new(baseTime.AddMinutes(1), 51000, 51100, 50900, 51000, 100), // tick 1
            new(baseTime.AddMinutes(2), 53000, 53100, 52900, 53000, 100), // tick 2: +6% → take-profit
            new(baseTime.AddMinutes(3), 54000, 54100, 53900, 54000, 100), // tick 3
        };

        var buySignal = new SignalGeneratedEvent(
            strategy.Id, symbol, OrderSide.Buy,
            Price.Create(50000m).Value, "RSI(14)=25.0000");

        _strategy.ProcessTickAsync(
            Arg.Is<MarketTickReceivedEvent>(t => t.Timestamp == klines[0].OpenTime),
            Arg.Any<CancellationToken>())
            .Returns(Result<SignalGeneratedEvent?, DomainError>.Success(buySignal));

        _strategy.ProcessTickAsync(
            Arg.Is<MarketTickReceivedEvent>(t => t.Timestamp != klines[0].OpenTime),
            Arg.Any<CancellationToken>())
            .Returns(Result<SignalGeneratedEvent?, DomainError>.Success(null));

        var orderQty = Quantity.Create(0.001m).Value;
        var order = Order.Create(
            strategy.Id, symbol, OrderSide.Buy, OrderType.Market,
            orderQty, TradingMode.PaperTrading).Value;

        _ruleEngine.EvaluateAsync(strategy, buySignal, Arg.Any<CancellationToken>())
            .Returns(Result<Order?, DomainError>.Success(order));

        _ruleEngine.EvaluateExitRulesAsync(
            Arg.Any<TradingStrategy>(), Arg.Any<Position>(),
            Arg.Any<Price>(), Arg.Any<CancellationToken>())
            .Returns(Result<Order?, DomainError>.Success(null));

        var result = await _engine.RunAsync(strategy, _strategy, _ruleEngine, klines);

        result.TotalTrades.Should().Be(1);
        result.Trades[0].ExitReason.Should().Be("Take-profit");
        result.Trades[0].PnL.Should().BePositive();
        result.WinningTrades.Should().Be(1);
    }
}
