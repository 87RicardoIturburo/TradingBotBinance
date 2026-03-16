using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TradingBot.Application.Diagnostics;
using TradingBot.Application.RiskManagement;
using TradingBot.Core.Common;
using TradingBot.Core.Entities;
using TradingBot.Core.Enums;
using TradingBot.Core.Interfaces;
using TradingBot.Core.Interfaces.Repositories;
using TradingBot.Core.Interfaces.Services;
using TradingBot.Core.ValueObjects;

namespace TradingBot.Application.Services;

/// <summary>
/// Implementación del servicio de órdenes. Valida con el RiskManager,
/// persiste la orden y la ejecuta o simula según el modo de trading.
/// </summary>
internal sealed class OrderService : IOrderService
{
    private readonly IOrderRepository _orderRepository;
    private readonly IPositionRepository _positionRepository;
    private readonly IUnitOfWork _unitOfWork;
    private readonly IRiskManager _riskManager;
    private readonly IMarketDataService _marketDataService;
    private readonly ISpotOrderExecutor _spotOrderExecutor;
    private readonly IAccountService _accountService;
    private readonly IExchangeInfoService _exchangeInfoService;
    private readonly IOrderExecutionLock _executionLock;
    private readonly IGlobalCircuitBreaker _circuitBreaker;
    private readonly TradingMetrics _metrics;
    private readonly TradingFeeConfig _feeConfig;
    private readonly ILogger<OrderService> _logger;

    public OrderService(
        IOrderRepository orderRepository,
        IPositionRepository positionRepository,
        IUnitOfWork unitOfWork,
        IRiskManager riskManager,
        IMarketDataService marketDataService,
        ISpotOrderExecutor spotOrderExecutor,
        IAccountService accountService,
        IExchangeInfoService exchangeInfoService,
        IOrderExecutionLock executionLock,
        IGlobalCircuitBreaker circuitBreaker,
        TradingMetrics metrics,
        IOptions<TradingFeeConfig> feeConfig,
        ILogger<OrderService> logger)
    {
        _orderRepository     = orderRepository;
        _positionRepository  = positionRepository;
        _unitOfWork          = unitOfWork;
        _riskManager         = riskManager;
        _marketDataService   = marketDataService;
        _spotOrderExecutor   = spotOrderExecutor;
        _accountService      = accountService;
        _exchangeInfoService = exchangeInfoService;
        _executionLock       = executionLock;
        _circuitBreaker      = circuitBreaker;
        _metrics             = metrics;
        _feeConfig           = feeConfig.Value;
        _logger              = logger;
    }

    public async Task<Result<Order, DomainError>> PlaceOrderAsync(
        Order order,
        CancellationToken cancellationToken = default)
    {
        // Circuit breaker global — si está abierto, rechazar todas las órdenes
        if (_circuitBreaker.IsOpen)
            return Result<Order, DomainError>.Failure(
                DomainError.RiskLimitExceeded(
                    $"Circuit breaker abierto: {_circuitBreaker.TripReason}"));

        // Serializar validación + ejecución por quote asset para evitar race conditions de saldo
        var quoteAsset = ExtractQuoteAsset(order.Symbol.Value);
        using var executionLockHandle = await _executionLock.AcquireAsync(quoteAsset, cancellationToken);

        // 1. Validar con RiskManager (obligatorio)
        var riskResult = await _riskManager.ValidateOrderAsync(order, cancellationToken);
        if (riskResult.IsFailure)
        {
            _metrics.RecordOrderFailed(order.Symbol.Value, "risk_validation");
            return Result<Order, DomainError>.Failure(riskResult.Error);
        }

        // 2. Validar y ajustar contra filtros del exchange (LOT_SIZE, PRICE_FILTER, MIN_NOTIONAL)
        var filterResult = await ValidateExchangeFiltersAsync(order, cancellationToken);
        if (filterResult.IsFailure)
        {
            _metrics.RecordOrderFailed(order.Symbol.Value, "exchange_filter");
            return Result<Order, DomainError>.Failure(filterResult.Error);
        }

        // 3. Validar spread bid-ask para órdenes Market (protección contra flash crash)
        if (order.LimitPrice is null)
        {
            var spreadResult = ValidateSpread(order);
            if (spreadResult.IsFailure)
            {
                _metrics.RecordOrderFailed(order.Symbol.Value, "spread_guard");
                return Result<Order, DomainError>.Failure(spreadResult.Error);
            }
        }

        // 4. Persistir la orden
        await _orderRepository.AddAsync(order, cancellationToken);

        // 5. Ejecutar según modo
        if (order.IsPaperTrade)
        {
            return await SimulatePaperTradeAsync(order, cancellationToken);
        }

        // Live/Testnet → ejecutar en Binance Spot
        return await ExecuteSpotOrderAsync(order, cancellationToken);
    }

    public async Task<Result<Order, DomainError>> CancelOrderAsync(
        Guid orderId,
        CancellationToken cancellationToken = default)
    {
        var order = await _orderRepository.GetByIdAsync(orderId, cancellationToken);
        if (order is null)
            return Result<Order, DomainError>.Failure(
                DomainError.NotFound($"Orden '{orderId}'"));

        // Si la orden tiene ID de Binance y no es paper trade, cancelar en el exchange primero
        if (!order.IsPaperTrade && !string.IsNullOrEmpty(order.BinanceOrderId))
        {
            var cancelResult = await _spotOrderExecutor.CancelOrderAsync(
                order.Symbol.Value, order.BinanceOrderId, cancellationToken);

            if (cancelResult.IsFailure)
            {
                _logger.LogWarning(
                    "No se pudo cancelar orden {OrderId} en Binance: {Error}. Cancelando localmente.",
                    orderId, cancelResult.Error.Message);
            }
        }

        var result = order.Cancel("Cancelada por el usuario.");
        if (result.IsFailure)
            return result;

        await _orderRepository.UpdateAsync(order, cancellationToken);
        await _unitOfWork.SaveChangesAsync(cancellationToken);

        _logger.LogInformation("Orden {OrderId} cancelada", orderId);
        return result;
    }

    public async Task<Result<Order, DomainError>> SyncOrderStatusAsync(
        Guid orderId,
        CancellationToken cancellationToken = default)
    {
        var order = await _orderRepository.GetByIdAsync(orderId, cancellationToken);
        if (order is null)
            return Result<Order, DomainError>.Failure(
                DomainError.NotFound($"Orden '{orderId}'"));

        // Paper trades no necesitan sincronización
        if (order.IsPaperTrade || order.IsTerminal || string.IsNullOrEmpty(order.BinanceOrderId))
            return Result<Order, DomainError>.Success(order);

        var statusResult = await _spotOrderExecutor.GetOrderStatusAsync(
            order.Symbol.Value, order.BinanceOrderId, cancellationToken);

        if (statusResult.IsFailure)
        {
            _logger.LogWarning(
                "No se pudo sincronizar orden {OrderId}: {Error}",
                orderId, statusResult.Error.Message);
            return Result<Order, DomainError>.Success(order);
        }

        var status = statusResult.Value;
        var changed = false;

        if (status.IsCompletelyFilled && order.Status != OrderStatus.Filled)
        {
            var qty = Quantity.Create(status.ExecutedQuantity);
            var price = Price.Create(status.ExecutedPrice);

            if (qty.IsSuccess && price.IsSuccess)
            {
                order.Fill(qty.Value, price.Value);
                await HandlePositionAsync(order, cancellationToken);
                changed = true;

                _logger.LogInformation(
                    "Orden {OrderId} sincronizada: Filled @ {Price}",
                    orderId, status.ExecutedPrice);
            }
        }
        else if (status.IsCancelled && order.Status != OrderStatus.Cancelled)
        {
            order.Cancel($"Cancelada en Binance (status: {status.Status})");
            changed = true;
        }
        else if (status.ExecutedQuantity > 0 && order.Status == OrderStatus.Submitted)
        {
            var qty = Quantity.Create(status.ExecutedQuantity);
            var price = Price.Create(status.ExecutedPrice);

            if (qty.IsSuccess && price.IsSuccess)
            {
                order.PartialFill(qty.Value, price.Value);
                changed = true;
            }
        }

        if (changed)
        {
            await _orderRepository.UpdateAsync(order, cancellationToken);
            await _unitOfWork.SaveChangesAsync(cancellationToken);
        }

        return Result<Order, DomainError>.Success(order);
    }

    public async Task<Result<IReadOnlyList<Order>, DomainError>> GetOpenOrdersAsync(
        CancellationToken cancellationToken = default)
    {
        var orders = await _orderRepository.GetOpenOrdersAsync(cancellationToken);
        return Result<IReadOnlyList<Order>, DomainError>.Success(orders);
    }

    /// <summary>
    /// Valida y ajusta cantidad/precio contra los filtros del exchange (LOT_SIZE, PRICE_FILTER, MIN_NOTIONAL).
    /// Si los filtros no están disponibles (p. ej. exchange caído), la orden se permite sin ajustar.
    /// </summary>
    private async Task<Result<bool, DomainError>> ValidateExchangeFiltersAsync(
        Order order,
        CancellationToken cancellationToken)
    {
        var filtersResult = await _exchangeInfoService.GetFiltersAsync(
            order.Symbol.Value, cancellationToken);

        if (filtersResult.IsFailure)
        {
            _logger.LogWarning(
                "No se pudieron obtener filtros de exchange para {Symbol}: {Error}. Continuando sin validar filtros.",
                order.Symbol.Value, filtersResult.Error.Message);
            return Result<bool, DomainError>.Success(true);
        }

        var filters = filtersResult.Value;
        var validateResult = filters.ValidateAndAdjust(
            order.Quantity.Value,
            order.LimitPrice?.Value);

        if (validateResult.IsFailure)
        {
            _logger.LogWarning(
                "Orden {OrderId} rechazada por filtros de exchange: {Error}",
                order.Id, validateResult.Error.Message);
            return Result<bool, DomainError>.Failure(validateResult.Error);
        }

        var (adjustedQty, adjustedPrice) = validateResult.Value;

        if (adjustedQty != order.Quantity.Value || adjustedPrice != order.LimitPrice?.Value)
        {
            _logger.LogDebug(
                "Orden {OrderId} ajustada por filtros de exchange: qty {OrigQty}→{AdjQty}, price {OrigPrice}→{AdjPrice}",
                order.Id, order.Quantity.Value, adjustedQty,
                order.LimitPrice?.Value, adjustedPrice);

            order.AdjustForExchange(adjustedQty, adjustedPrice);
        }

        return Result<bool, DomainError>.Success(true);
    }

    private async Task<Result<Order, DomainError>> ExecuteSpotOrderAsync(
        Order order,
        CancellationToken cancellationToken)
    {
        // Enviar al exchange
        var exchangeResult = await _spotOrderExecutor.PlaceOrderAsync(order, cancellationToken);

        if (exchangeResult.IsFailure)
        {
            // El exchange rechazó la orden antes de aceptarla
            order.Submit();
            order.Reject(exchangeResult.Error.Message);
            await _unitOfWork.SaveChangesAsync(cancellationToken);

            _metrics.RecordOrderFailed(order.Symbol.Value, "exchange_rejected");
            _logger.LogWarning(
                "Orden {OrderId} rechazada por Binance: {Error}",
                order.Id, exchangeResult.Error.Message);

            return Result<Order, DomainError>.Failure(exchangeResult.Error);
        }

        var spotResult = exchangeResult.Value;

        // Marcar como Submitted con el ID de Binance
        var submitResult = order.Submit(spotResult.ExchangeOrderId);
        if (submitResult.IsFailure)
        {
            await _unitOfWork.SaveChangesAsync(cancellationToken);
            return submitResult;
        }

        // Si la orden de Market se llenó inmediatamente (lo esperado)
        if (spotResult.ExecutedQuantity > 0 && spotResult.Status is "Filled")
        {
            var qty = Quantity.Create(spotResult.ExecutedQuantity);
            var price = Price.Create(spotResult.ExecutedPrice);

            if (qty.IsSuccess && price.IsSuccess)
            {
                order.Fill(qty.Value, price.Value);
                await HandlePositionAsync(order, cancellationToken);
            }
        }
        else if (spotResult.ExecutedQuantity > 0)
        {
            // Fill parcial (raro en Market, posible en Limit)
            var qty = Quantity.Create(spotResult.ExecutedQuantity);
            var price = Price.Create(spotResult.ExecutedPrice);

            if (qty.IsSuccess && price.IsSuccess)
                order.PartialFill(qty.Value, price.Value);
        }

        await _unitOfWork.SaveChangesAsync(cancellationToken);

        // Invalidar caché de balance — el saldo cambió tras la orden
        await _accountService.InvalidateCacheAsync(cancellationToken);

        _metrics.RecordOrderPlaced(order.Symbol.Value, order.Side.ToString(), order.Type.ToString(), isPaper: false);
        _logger.LogInformation(
            "Orden {OrderId} ejecutada en Binance: {Side} {Qty} {Symbol} BinanceId={BinanceId} Status={Status}",
            order.Id, order.Side, order.Quantity.Value, order.Symbol.Value,
            spotResult.ExchangeOrderId, spotResult.Status);

        return Result<Order, DomainError>.Success(order);
    }

    private async Task<Result<Order, DomainError>> SimulatePaperTradeAsync(
        Order order,
        CancellationToken cancellationToken)
    {
        var submitResult = order.Submit($"PAPER-{Guid.NewGuid():N}"[..20]);
        if (submitResult.IsFailure)
            return submitResult;

        // Determinar el precio de ejecución:
        // - Limit orders: usar el LimitPrice (sin slippage)
        // - Market orders: obtener precio actual de Binance REST + slippage
        Price executionPrice;
        var isMarketOrder = order.LimitPrice is null;
        if (order.LimitPrice is not null)
        {
            executionPrice = order.LimitPrice;
        }
        else
        {
            var priceResult = await _marketDataService.GetCurrentPriceAsync(
                order.Symbol, cancellationToken);

            if (priceResult.IsFailure)
            {
                _logger.LogWarning(
                    "No se pudo obtener precio para paper trade {OrderId}: {Error}. Orden queda como Submitted.",
                    order.Id, priceResult.Error.Message);
                await _unitOfWork.SaveChangesAsync(cancellationToken);
                return Result<Order, DomainError>.Success(order);
            }

            // Aplicar slippage al precio de mercado
            var rawPrice = priceResult.Value.Value;
            var slippedPrice = FeeAndSlippageCalculator.ApplySlippage(
                rawPrice, order.Side, _feeConfig.SlippagePercent);
            executionPrice = Price.Create(slippedPrice).Value;
        }

        // Calcular fee
        var feePercent = isMarketOrder ? _feeConfig.EffectiveTakerFee : _feeConfig.EffectiveMakerFee;
        var fee = FeeAndSlippageCalculator.CalculateFee(
            executionPrice.Value, order.Quantity.Value, feePercent);

        _logger.LogDebug(
            "Paper trade fees: {Fee:F4} USDT (rate={Rate:P3}, slippage={Slippage:P3})",
            fee, feePercent, isMarketOrder ? _feeConfig.SlippagePercent : 0m);

        // En paper trading se llena inmediatamente
        var fillResult = order.Fill(order.Quantity, executionPrice, fee);
        if (fillResult.IsFailure)
        {
            await _unitOfWork.SaveChangesAsync(cancellationToken);
            return Result<Order, DomainError>.Success(order);
        }

        // Crear o cerrar posición según el lado
        await HandlePositionAsync(order, cancellationToken);
        await _unitOfWork.SaveChangesAsync(cancellationToken);

        _metrics.RecordOrderPlaced(order.Symbol.Value, order.Side.ToString(), order.Type.ToString(), isPaper: true);
        _logger.LogInformation(
            "Paper trade completado: {Side} {Qty} {Symbol} @ {Price} (fee: {Fee:F4} USDT)",
            order.Side, order.Quantity.Value, order.Symbol.Value,
            order.ExecutedPrice?.Value, order.Fee);

        return Result<Order, DomainError>.Success(order);
    }

    private async Task HandlePositionAsync(Order order, CancellationToken cancellationToken)
    {
        // Buscar si hay posición abierta del lado opuesto para cerrarla
        var oppositeSide = order.Side == OrderSide.Buy ? OrderSide.Sell : OrderSide.Buy;
        var openPositions = await _positionRepository
            .GetOpenByStrategyIdAsync(order.StrategyId, cancellationToken);

        var positionToClose = openPositions
            .FirstOrDefault(p => p.Symbol == order.Symbol && p.Side == oppositeSide);

        if (positionToClose is not null && order.ExecutedPrice is not null)
        {
            // Cerrar posición existente del lado opuesto, descontando fee de salida
            positionToClose.Close(order.ExecutedPrice, order.Fee);
            await _positionRepository.UpdateAsync(positionToClose, cancellationToken);

            _logger.LogInformation(
                "Posición {PosId} cerrada: {Side} {Symbol} PnL={PnL:F2} (fees: entry={EntryFee:F4}, exit={ExitFee:F4})",
                positionToClose.Id, positionToClose.Side, order.Symbol.Value,
                positionToClose.RealizedPnL, positionToClose.EntryFee, positionToClose.ExitFee);
        }
        else if (order.Side == OrderSide.Buy)
        {
            // Abrir nueva posición Long (Spot solo permite Long), con fee de entrada
            var position = Position.Open(
                order.StrategyId, order.Symbol, order.Side,
                order.ExecutedPrice!, order.FilledQuantity!,
                order.Fee);

            await _positionRepository.AddAsync(position, cancellationToken);
        }
        else
        {
            // Sell sin posición Long que cerrar — no se puede abrir Short en Spot
            _logger.LogWarning(
                "Orden Sell {OrderId} completada pero no hay posición Long abierta para cerrar en {Symbol}. " +
                "No se crea posición Short (Spot trading no soporta shorts).",
                order.Id, order.Symbol.Value);
        }
    }

    /// <summary>Extrae el quote asset del símbolo (BTCUSDT → USDT).</summary>
    private static string ExtractQuoteAsset(string symbol)
    {
        ReadOnlySpan<char> s = symbol;
        string[] quoteAssets = ["USDT", "BUSD", "USDC", "BTC", "ETH", "BNB"];
        foreach (var qa in quoteAssets)
        {
            if (s.EndsWith(qa, StringComparison.OrdinalIgnoreCase))
                return qa;
        }
        return "USDT"; // fallback conservador
    }

    /// <summary>
    /// Valida que el spread bid-ask no exceda el máximo configurado.
    /// Solo aplica a órdenes Market. Si no hay datos de bid/ask disponibles, se permite la orden con warning.
    /// </summary>
    private Result<bool, DomainError> ValidateSpread(Order order)
    {
        var bidAsk = _marketDataService.GetLastBidAsk(order.Symbol);
        if (bidAsk is null)
        {
            _logger.LogDebug(
                "Sin datos bid/ask para {Symbol}, spread guard omitido", order.Symbol.Value);
            return Result<bool, DomainError>.Success(true);
        }

        var (bid, ask) = bidAsk.Value;
        if (bid.Value <= 0 || ask.Value <= 0)
            return Result<bool, DomainError>.Success(true);

        var midPrice = (bid.Value + ask.Value) / 2m;
        var spreadPercent = (ask.Value - bid.Value) / midPrice * 100m;

        // Usar un default de 1.0% si no se puede determinar MaxSpreadPercent de la estrategia
        const decimal defaultMaxSpread = 1.0m;

        if (spreadPercent > defaultMaxSpread)
        {
            _logger.LogWarning(
                "⚠ Spread excesivo para {Symbol}: {Spread:F3}% (máx {Max:F1}%). Orden {OrderId} rechazada.",
                order.Symbol.Value, spreadPercent, defaultMaxSpread, order.Id);
            return Result<bool, DomainError>.Failure(
                DomainError.RiskLimitExceeded(
                    $"Spread bid-ask de {spreadPercent:F3}% excede el máximo de {defaultMaxSpread:F1}% para {order.Symbol.Value}."));
        }

        if (spreadPercent > defaultMaxSpread * 0.5m)
        {
            _logger.LogWarning(
                "⚠ Spread elevado para {Symbol}: {Spread:F3}% (advertencia > {Warn:F1}%)",
                order.Symbol.Value, spreadPercent, defaultMaxSpread * 0.5m);
        }

        return Result<bool, DomainError>.Success(true);
    }
}
