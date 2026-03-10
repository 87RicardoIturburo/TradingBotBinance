using Microsoft.Extensions.Logging;
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
    private readonly ILogger<OrderService> _logger;

    public OrderService(
        IOrderRepository orderRepository,
        IPositionRepository positionRepository,
        IUnitOfWork unitOfWork,
        IRiskManager riskManager,
        IMarketDataService marketDataService,
        ISpotOrderExecutor spotOrderExecutor,
        ILogger<OrderService> logger)
    {
        _orderRepository = orderRepository;
        _positionRepository = positionRepository;
        _unitOfWork = unitOfWork;
        _riskManager = riskManager;
        _marketDataService = marketDataService;
        _spotOrderExecutor = spotOrderExecutor;
        _logger = logger;
    }

    public async Task<Result<Order, DomainError>> PlaceOrderAsync(
        Order order,
        CancellationToken cancellationToken = default)
    {
        // 1. Validar con RiskManager (obligatorio)
        var riskResult = await _riskManager.ValidateOrderAsync(order, cancellationToken);
        if (riskResult.IsFailure)
            return Result<Order, DomainError>.Failure(riskResult.Error);

        // 2. Persistir la orden
        await _orderRepository.AddAsync(order, cancellationToken);

        // 3. Ejecutar según modo
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
        // - Limit orders: usar el LimitPrice
        // - Market orders: obtener precio actual de Binance REST
        Price executionPrice;
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

            executionPrice = priceResult.Value;
        }

        // En paper trading se llena inmediatamente
        var fillResult = order.Fill(order.Quantity, executionPrice);
        if (fillResult.IsFailure)
        {
            await _unitOfWork.SaveChangesAsync(cancellationToken);
            return Result<Order, DomainError>.Success(order);
        }

        // Crear o cerrar posición según el lado
        await HandlePositionAsync(order, cancellationToken);
        await _unitOfWork.SaveChangesAsync(cancellationToken);

        _logger.LogInformation(
            "Paper trade completado: {Side} {Qty} {Symbol} @ {Price}",
            order.Side, order.Quantity.Value, order.Symbol.Value,
            order.ExecutedPrice?.Value);

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
            // Cerrar posición existente del lado opuesto
            positionToClose.Close(order.ExecutedPrice);
            await _positionRepository.UpdateAsync(positionToClose, cancellationToken);

            _logger.LogInformation(
                "Posición {PosId} cerrada: {Side} {Symbol} PnL={PnL:F2}",
                positionToClose.Id, positionToClose.Side, order.Symbol.Value, positionToClose.RealizedPnL);
        }
        else
        {
            // Abrir nueva posición
            var position = Position.Open(
                order.StrategyId, order.Symbol, order.Side,
                order.ExecutedPrice!, order.FilledQuantity!);

            await _positionRepository.AddAsync(position, cancellationToken);
        }
    }
}
