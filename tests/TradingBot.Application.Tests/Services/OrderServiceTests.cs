using System.Diagnostics.Metrics;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using TradingBot.Application.Diagnostics;
using TradingBot.Application.RiskManagement;
using TradingBot.Application.Services;
using TradingBot.Core.Common;
using TradingBot.Core.Entities;
using TradingBot.Core.Enums;
using TradingBot.Core.Interfaces;
using TradingBot.Core.Interfaces.Repositories;
using TradingBot.Core.Interfaces.Services;
using TradingBot.Core.ValueObjects;

namespace TradingBot.Application.Tests.Services;

public sealed class OrderServiceTests
{
    private readonly IOrderRepository    _orderRepo    = Substitute.For<IOrderRepository>();
    private readonly IUnitOfWork         _unitOfWork   = Substitute.For<IUnitOfWork>();
    private readonly IRiskManager        _riskManager  = Substitute.For<IRiskManager>();
    private readonly IMarketDataService  _marketData   = Substitute.For<IMarketDataService>();
    private readonly ISpotOrderExecutor  _spotExecutor = Substitute.For<ISpotOrderExecutor>();
    private readonly IAccountService     _accountService = Substitute.For<IAccountService>();
    private readonly IExchangeInfoService _exchangeInfo = Substitute.For<IExchangeInfoService>();
    private readonly IOrderExecutionLock _executionLock = Substitute.For<IOrderExecutionLock>();
    private readonly IGlobalCircuitBreaker _circuitBreaker = Substitute.For<IGlobalCircuitBreaker>();
    private readonly IOrderSyncHandler   _orderSyncHandler = Substitute.For<IOrderSyncHandler>();
    private readonly TradingMetrics        _metrics;
    private readonly OrderService        _sut;

    public OrderServiceTests()
    {
        var meterFactory = Substitute.For<IMeterFactory>();
        meterFactory.Create(Arg.Any<MeterOptions>()).Returns(new Meter("TradingBot.Test"));
        _metrics = new TradingMetrics(meterFactory);

        // By default, exchange filters are unavailable (graceful degradation)
        _exchangeInfo.GetFiltersAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Result<ExchangeSymbolFilters, DomainError>.Failure(
                DomainError.ExternalService("Filtros no disponibles")));

        // Execution lock always succeeds immediately
        _executionLock.AcquireAsync(Arg.Any<string>(), Arg.Any<CancellationToken>(), Arg.Any<TimeSpan?>())
            .Returns(Substitute.For<IDisposable>());

        // Circuit breaker closed by default
        _circuitBreaker.IsOpen.Returns(false);

        _sut = new OrderService(
            _orderRepo, _unitOfWork,
            _riskManager, _marketData, _spotExecutor,
            _accountService, _exchangeInfo, _executionLock,
            _circuitBreaker, _orderSyncHandler, _metrics,
            Options.Create(new TradingFeeConfig()),
            NullLogger<OrderService>.Instance);
    }

    // ── PlaceOrderAsync ───────────────────────────────────────────────────

    [Fact]
    public async Task PlaceOrderAsync_WhenRiskValidationFails_ReturnsFailure()
    {
        var order = CreatePaperOrder();
        _riskManager.ValidateOrderAsync(order, Arg.Any<CancellationToken>())
            .Returns(Result<bool, DomainError>.Failure(
                DomainError.RiskLimitExceeded("Monto excedido")));

        var result = await _sut.PlaceOrderAsync(order);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("RISK_LIMIT_EXCEEDED");
        await _orderRepo.DidNotReceive().AddAsync(Arg.Any<Order>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task PlaceOrderAsync_WhenPaperTrade_SimulatesAndFillsImmediately()
    {
        var order = CreatePaperOrder();
        SetupRiskApproved(order);
        SetupMarketPrice(55000m);

        var result = await _sut.PlaceOrderAsync(order);

        result.IsSuccess.Should().BeTrue();
        result.Value.Status.Should().Be(OrderStatus.Filled);
        result.Value.ExecutedPrice.Should().NotBeNull();
        await _orderRepo.Received(1).AddAsync(order, Arg.Any<CancellationToken>());
        await _unitOfWork.Received().SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task PlaceOrderAsync_WhenPaperLimitOrder_UsesLimitPrice()
    {
        var limitPrice = Price.Create(54000m).Value;
        var order = CreatePaperOrder(OrderType.Limit, limitPrice: limitPrice);
        SetupRiskApproved(order);

        // Paper Limit: se consulta precio de mercado para verificar si puede llenarse.
        // Buy Limit se llena cuando marketPrice <= limitPrice.
        _marketData.GetCurrentPriceAsync(Arg.Any<Symbol>(), Arg.Any<CancellationToken>())
            .Returns(Result<Price, DomainError>.Success(Price.Create(53500m).Value));

        var result = await _sut.PlaceOrderAsync(order);

        result.IsSuccess.Should().BeTrue();
        result.Value.Status.Should().Be(OrderStatus.Filled);
        result.Value.ExecutedPrice!.Value.Should().Be(54000m);
    }

    [Fact]
    public async Task PlaceOrderAsync_WhenPaperMarketPriceFails_LeavesAsSubmitted()
    {
        var order = CreatePaperOrder();
        SetupRiskApproved(order);
        _marketData.GetCurrentPriceAsync(Arg.Any<Symbol>(), Arg.Any<CancellationToken>())
            .Returns(Result<Price, DomainError>.Failure(
                DomainError.ExternalService("No se pudo obtener precio")));

        var result = await _sut.PlaceOrderAsync(order);

        result.IsSuccess.Should().BeTrue();
        result.Value.Status.Should().Be(OrderStatus.Submitted);
        await _unitOfWork.Received().SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task PlaceOrderAsync_WhenPaperBuyOrder_CallsOrderSyncHandler()
    {
        var order = CreatePaperOrder(side: OrderSide.Buy);
        SetupRiskApproved(order);
        SetupMarketPrice(55000m);

        await _sut.PlaceOrderAsync(order);

        await _orderSyncHandler.Received(1).HandleOrderFilledAsync(
            Arg.Is<Order>(o => o.Side == OrderSide.Buy && o.Status == OrderStatus.Filled),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task PlaceOrderAsync_WhenPaperSellOrder_CallsOrderSyncHandler()
    {
        var strategyId = Guid.NewGuid();
        var order = CreatePaperOrder(side: OrderSide.Sell, strategyId: strategyId);
        SetupRiskApproved(order);
        SetupMarketPrice(55000m);

        await _sut.PlaceOrderAsync(order);

        await _orderSyncHandler.Received(1).HandleOrderFilledAsync(
            Arg.Is<Order>(o => o.Side == OrderSide.Sell && o.Status == OrderStatus.Filled),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task PlaceOrderAsync_WhenLiveMarketOrder_ExecutesOnBinanceAndFills()
    {
        var order = CreateLiveOrder();
        SetupRiskApproved(order);

        _spotExecutor.PlaceOrderAsync(order, Arg.Any<CancellationToken>())
            .Returns(Result<SpotOrderResult, DomainError>.Success(
                new SpotOrderResult("12345", 0.01m, 55000m, "Filled")));

        var result = await _sut.PlaceOrderAsync(order);

        result.IsSuccess.Should().BeTrue();
        result.Value.Status.Should().Be(OrderStatus.Filled);
        result.Value.BinanceOrderId.Should().Be("12345");
        result.Value.ExecutedPrice!.Value.Should().Be(55000m);
        await _unitOfWork.Received().SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task PlaceOrderAsync_WhenLiveOrderRejectedByBinance_ReturnsFailure()
    {
        var order = CreateLiveOrder();
        SetupRiskApproved(order);

        _spotExecutor.PlaceOrderAsync(order, Arg.Any<CancellationToken>())
            .Returns(Result<SpotOrderResult, DomainError>.Failure(
                DomainError.ExternalService("Insufficient balance")));

        var result = await _sut.PlaceOrderAsync(order);

        result.IsFailure.Should().BeTrue();
        result.Error.Message.Should().Contain("Insufficient balance");
    }

    [Fact]
    public async Task PlaceOrderAsync_WhenLiveLimitOrder_StaysSubmitted()
    {
        var limitPrice = Price.Create(50000m).Value;
        var order = CreateLiveOrder(OrderType.Limit, limitPrice);
        SetupRiskApproved(order);

        _spotExecutor.PlaceOrderAsync(order, Arg.Any<CancellationToken>())
            .Returns(Result<SpotOrderResult, DomainError>.Success(
                new SpotOrderResult("12346", 0m, 0m, "New")));

        var result = await _sut.PlaceOrderAsync(order);

        result.IsSuccess.Should().BeTrue();
        result.Value.Status.Should().Be(OrderStatus.Submitted);
        result.Value.BinanceOrderId.Should().Be("12346");
    }

    // ── CancelOrderAsync ──────────────────────────────────────────────────

    [Fact]
    public async Task CancelOrderAsync_WhenOrderNotFound_ReturnsNotFoundError()
    {
        var orderId = Guid.NewGuid();
        _orderRepo.GetByIdAsync(orderId, Arg.Any<CancellationToken>())
            .Returns((Order?)null);

        var result = await _sut.CancelOrderAsync(orderId);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("NOT_FOUND");
    }

    [Fact]
    public async Task CancelOrderAsync_WhenOrderIsPending_CancelsSuccessfully()
    {
        var order = CreatePaperOrder();
        _orderRepo.GetByIdAsync(order.Id, Arg.Any<CancellationToken>())
            .Returns(order);

        var result = await _sut.CancelOrderAsync(order.Id);

        result.IsSuccess.Should().BeTrue();
        result.Value.Status.Should().Be(OrderStatus.Cancelled);
        await _orderRepo.Received(1).UpdateAsync(order, Arg.Any<CancellationToken>());
        await _unitOfWork.Received().SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CancelOrderAsync_WhenOrderAlreadyFilled_ReturnsFailure()
    {
        var order = CreatePaperOrder();
        order.Submit("PAPER-TEST");
        order.Fill(order.Quantity, Price.Create(55000m).Value);

        _orderRepo.GetByIdAsync(order.Id, Arg.Any<CancellationToken>())
            .Returns(order);

        var result = await _sut.CancelOrderAsync(order.Id);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("INVALID_OPERATION");
    }

    // ── SyncOrderStatusAsync ──────────────────────────────────────────────

    [Fact]
    public async Task SyncOrderStatusAsync_WhenOrderNotFound_ReturnsNotFoundError()
    {
        var orderId = Guid.NewGuid();
        _orderRepo.GetByIdAsync(orderId, Arg.Any<CancellationToken>())
            .Returns((Order?)null);

        var result = await _sut.SyncOrderStatusAsync(orderId);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("NOT_FOUND");
    }

    [Fact]
    public async Task SyncOrderStatusAsync_WhenOrderExists_ReturnsOrder()
    {
        var order = CreatePaperOrder();
        _orderRepo.GetByIdAsync(order.Id, Arg.Any<CancellationToken>())
            .Returns(order);

        var result = await _sut.SyncOrderStatusAsync(order.Id);

        result.IsSuccess.Should().BeTrue();
        result.Value.Id.Should().Be(order.Id);
    }

    [Fact]
    public async Task SyncOrderStatusAsync_WhenBinanceOrderFilled_UpdatesLocalOrder()
    {
        var order = CreateLiveOrder();
        order.Submit("99999");
        _orderRepo.GetByIdAsync(order.Id, Arg.Any<CancellationToken>())
            .Returns(order);

        _spotExecutor.GetOrderStatusAsync("BTCUSDT", "99999", Arg.Any<CancellationToken>())
            .Returns(Result<SpotOrderStatus, DomainError>.Success(
                new SpotOrderStatus("99999", 0.01m, 55000m, "Filled", true, false)));

        var result = await _sut.SyncOrderStatusAsync(order.Id);

        result.IsSuccess.Should().BeTrue();
        result.Value.Status.Should().Be(OrderStatus.Filled);
        result.Value.ExecutedPrice!.Value.Should().Be(55000m);
        await _unitOfWork.Received().SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SyncOrderStatusAsync_WhenBinanceOrderCancelled_CancelsLocalOrder()
    {
        var order = CreateLiveOrder();
        order.Submit("99998");
        _orderRepo.GetByIdAsync(order.Id, Arg.Any<CancellationToken>())
            .Returns(order);

        _spotExecutor.GetOrderStatusAsync("BTCUSDT", "99998", Arg.Any<CancellationToken>())
            .Returns(Result<SpotOrderStatus, DomainError>.Success(
                new SpotOrderStatus("99998", 0m, 0m, "Canceled", false, true)));

        var result = await _sut.SyncOrderStatusAsync(order.Id);

        result.IsSuccess.Should().BeTrue();
        result.Value.Status.Should().Be(OrderStatus.Cancelled);
    }

    // ── GetOpenOrdersAsync ────────────────────────────────────────────────

    [Fact]
    public async Task GetOpenOrdersAsync_WhenCalled_ReturnsOrders()
    {
        var orders = new List<Order> { CreatePaperOrder(), CreatePaperOrder() };
        _orderRepo.GetOpenOrdersAsync(Arg.Any<CancellationToken>())
            .Returns(orders.AsReadOnly());

        var result = await _sut.GetOpenOrdersAsync();

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().HaveCount(2);
    }

    // ── Exchange Filter Validation ────────────────────────────────────────

    [Fact]
    public async Task PlaceOrderAsync_WhenExchangeFiltersUnavailable_ContinuesWithoutValidation()
    {
        var order = CreatePaperOrder();
        SetupRiskApproved(order);
        SetupMarketPrice(55000m);

        // Default setup: filters unavailable — should still succeed
        var result = await _sut.PlaceOrderAsync(order);

        result.IsSuccess.Should().BeTrue();
        result.Value.Status.Should().Be(OrderStatus.Filled);
    }

    [Fact]
    public async Task PlaceOrderAsync_WhenQuantityBelowMinQty_ReturnsFilterError()
    {
        var order = CreatePaperOrder();
        SetupRiskApproved(order);
        SetupExchangeFilters(minQty: 0.1m); // order qty is 0.01 < 0.1

        var result = await _sut.PlaceOrderAsync(order);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("VALIDATION_ERROR");
        result.Error.Message.Should().Contain("mínimo permitido");
    }

    [Fact]
    public async Task PlaceOrderAsync_WhenQuantityAboveMaxQty_ReturnsFilterError()
    {
        var order = CreatePaperOrder();
        SetupRiskApproved(order);
        SetupExchangeFilters(maxQty: 0.001m); // order qty is 0.01 > 0.001

        var result = await _sut.PlaceOrderAsync(order);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("VALIDATION_ERROR");
        result.Error.Message.Should().Contain("máximo permitido");
    }

    [Fact]
    public async Task PlaceOrderAsync_WhenFiltersPass_ProceedsNormally()
    {
        var order = CreatePaperOrder();
        SetupRiskApproved(order);
        SetupMarketPrice(55000m);
        SetupExchangeFilters(); // Default filters that allow 0.01 qty

        var result = await _sut.PlaceOrderAsync(order);

        result.IsSuccess.Should().BeTrue();
        result.Value.Status.Should().Be(OrderStatus.Filled);
    }

    [Fact]
    public async Task PlaceOrderAsync_WhenLimitNotionalBelowMinimum_ReturnsFilterError()
    {
        var limitPrice = Price.Create(5m).Value;
        var order = CreatePaperOrder(OrderType.Limit, limitPrice: limitPrice);
        SetupRiskApproved(order);
        // qty=0.01 * price=5 = 0.05 USDT notional < minNotional=10
        SetupExchangeFilters(minNotional: 10m);

        var result = await _sut.PlaceOrderAsync(order);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("VALIDATION_ERROR");
        result.Error.Message.Should().Contain("Notional");
    }

    // ── Helpers ────────────────────────────────────────────────────────────

    private static Order CreatePaperOrder(
        OrderType type = OrderType.Market,
        OrderSide side = OrderSide.Buy,
        Guid? strategyId = null,
        Price? limitPrice = null)
    {
        var symbol = Symbol.Create("BTCUSDT").Value;
        var qty    = Quantity.Create(0.01m).Value;
        return Order.Create(
            strategyId ?? Guid.NewGuid(), symbol, side, type,
            qty, TradingMode.PaperTrading, limitPrice).Value;
    }

    private static Order CreateLiveOrder(
        OrderType type = OrderType.Market,
        Price? limitPrice = null)
    {
        var symbol = Symbol.Create("BTCUSDT").Value;
        var qty    = Quantity.Create(0.01m).Value;
        return Order.Create(
            Guid.NewGuid(), symbol, OrderSide.Buy, type,
            qty, TradingMode.Live, limitPrice).Value;
    }

    private void SetupRiskApproved(Order order)
    {
        _riskManager.ValidateOrderAsync(order, Arg.Any<CancellationToken>())
            .Returns(Result<bool, DomainError>.Success(true));
    }

    private void SetupMarketPrice(decimal price)
    {
        _marketData.GetCurrentPriceAsync(Arg.Any<Symbol>(), Arg.Any<CancellationToken>())
            .Returns(Result<Price, DomainError>.Success(Price.Create(price).Value));
    }

    private void SetupExchangeFilters(
        decimal minQty = 0.00001m,
        decimal maxQty = 9000m,
        decimal stepSize = 0.00001m,
        decimal tickSize = 0.01m,
        decimal minNotional = 5m)
    {
        var filters = new ExchangeSymbolFilters(
            Symbol: "BTCUSDT",
            MinQty: minQty,
            MaxQty: maxQty,
            StepSize: stepSize,
            TickSize: tickSize,
            MinNotional: minNotional,
            MaxNumOrders: 200);

        _exchangeInfo.GetFiltersAsync("BTCUSDT", Arg.Any<CancellationToken>())
            .Returns(Result<ExchangeSymbolFilters, DomainError>.Success(filters));
    }

    // ── P0-3: Exchange filter adjustments applied ─────────────────────────

    [Fact]
    public async Task PlaceOrderAsync_WhenFiltersAdjustQuantity_AppliesAdjustmentToOrder()
    {
        // stepSize=0.001 → qty 0.01 stays as 0.01 (already a multiple)
        // stepSize=0.1 → qty 0.01 floors to 0.0 → below minQty → rejected
        // Use a quantity that gets adjusted but remains valid
        var qty = Quantity.Create(0.0155m).Value;
        var order = Order.Create(
            Guid.NewGuid(), Symbol.Create("BTCUSDT").Value, OrderSide.Buy,
            OrderType.Market, qty, TradingMode.PaperTrading).Value;

        SetupRiskApproved(order);
        SetupMarketPrice(55000m);
        SetupExchangeFilters(minQty: 0.001m, stepSize: 0.01m); // 0.0155 → floor to 0.01

        var result = await _sut.PlaceOrderAsync(order);

        result.IsSuccess.Should().BeTrue();
        // The order's quantity should have been adjusted to 0.01 (floor of 0.0155 with stepSize 0.01)
        result.Value.Quantity.Value.Should().Be(0.01m);
    }

    // ── P0-4: No phantom Short positions in Spot ──────────────────────────

    [Fact]
    public async Task PlaceOrderAsync_WhenSellOrderFilled_DelegatesToOrderSyncHandler()
    {
        var strategyId = Guid.NewGuid();
        var order = CreatePaperOrder(side: OrderSide.Sell, strategyId: strategyId);
        SetupRiskApproved(order);
        SetupMarketPrice(55000m);

        await _sut.PlaceOrderAsync(order);

        // La gestión de posiciones se delega a OrderSyncHandler
        await _orderSyncHandler.Received(1).HandleOrderFilledAsync(
            Arg.Is<Order>(o => o.Side == OrderSide.Sell && o.StrategyId == strategyId),
            Arg.Any<CancellationToken>());
    }

    // ── Dry-Run Mode ──────────────────────────────────────────────────────

    [Fact]
    public async Task PlaceOrderAsync_WhenDryRun_DoesNotExecuteOrSimulate()
    {
        var order = CreateDryRunOrder();
        SetupRiskApproved(order);

        var result = await _sut.PlaceOrderAsync(order);

        result.IsSuccess.Should().BeTrue();
        result.Value.Status.Should().Be(OrderStatus.Submitted);
        result.Value.BinanceOrderId.Should().StartWith("DRYRUN-");

        // No debe llamar al executor ni al sync handler
        await _spotExecutor.DidNotReceive()
            .PlaceOrderAsync(Arg.Any<Order>(), Arg.Any<CancellationToken>());
        await _orderSyncHandler.DidNotReceive()
            .HandleOrderFilledAsync(Arg.Any<Order>(), Arg.Any<CancellationToken>());
        // No debe llamar al market data service para obtener precio
        await _marketData.DidNotReceive()
            .GetCurrentPriceAsync(Arg.Any<Symbol>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task PlaceOrderAsync_WhenDryRunSell_DoesNotCreatePosition()
    {
        var order = CreateDryRunOrder(side: OrderSide.Sell);
        SetupRiskApproved(order);

        var result = await _sut.PlaceOrderAsync(order);

        result.IsSuccess.Should().BeTrue();
        await _orderSyncHandler.DidNotReceive()
            .HandleOrderFilledAsync(Arg.Any<Order>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task PlaceOrderAsync_WhenDryRun_StillValidatesRisk()
    {
        var order = CreateDryRunOrder();
        _riskManager.ValidateOrderAsync(order, Arg.Any<CancellationToken>())
            .Returns(Result<bool, DomainError>.Failure(
                DomainError.RiskLimitExceeded("Monto excedido")));

        var result = await _sut.PlaceOrderAsync(order);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("RISK_LIMIT_EXCEEDED");
    }

    // ── Helpers ────────────────────────────────────────────────────────────

    private static Order CreateDryRunOrder(
        OrderType type = OrderType.Market,
        OrderSide side = OrderSide.Buy,
        Guid? strategyId = null)
    {
        var symbol = Symbol.Create("BTCUSDT").Value;
        var qty    = Quantity.Create(0.01m).Value;
        return Order.Create(
            strategyId ?? Guid.NewGuid(), symbol, side, type,
            qty, TradingMode.DryRun).Value;
    }
}
