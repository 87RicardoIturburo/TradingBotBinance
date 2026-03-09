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
    private readonly ILogger<OrderService> _logger;

    public OrderService(
        IOrderRepository orderRepository,
        IPositionRepository positionRepository,
        IUnitOfWork unitOfWork,
        IRiskManager riskManager,
        IMarketDataService marketDataService,
        ILogger<OrderService> logger)
    {
        _orderRepository = orderRepository;
        _positionRepository = positionRepository;
        _unitOfWork = unitOfWork;
        _riskManager = riskManager;
        _marketDataService = marketDataService;
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

        // Para Live/Testnet, la orden se envía como Submitted
        // La ejecución real se hará en Infrastructure vía Binance.Net
        var submitResult = order.Submit();
        if (submitResult.IsFailure)
            return submitResult;

        await _unitOfWork.SaveChangesAsync(cancellationToken);

        _logger.LogInformation(
            "Orden {OrderId} enviada: {Side} {Type} {Qty} {Symbol}",
            order.Id, order.Side, order.Type, order.Quantity.Value, order.Symbol.Value);

        return Result<Order, DomainError>.Success(order);
    }

    public async Task<Result<Order, DomainError>> CancelOrderAsync(
        Guid orderId,
        CancellationToken cancellationToken = default)
    {
        var order = await _orderRepository.GetByIdAsync(orderId, cancellationToken);
        if (order is null)
            return Result<Order, DomainError>.Failure(
                DomainError.NotFound($"Orden '{orderId}'"));

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

        // La sincronización con Binance REST se implementará en Infrastructure
        // Por ahora devuelve la orden tal cual
        _logger.LogDebug("Sync de estado para orden {OrderId} (pendiente integración REST)", orderId);
        return Result<Order, DomainError>.Success(order);
    }

    public async Task<Result<IReadOnlyList<Order>, DomainError>> GetOpenOrdersAsync(
        CancellationToken cancellationToken = default)
    {
        var orders = await _orderRepository.GetOpenOrdersAsync(cancellationToken);
        return Result<IReadOnlyList<Order>, DomainError>.Success(orders);
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
        if (order.Side == OrderSide.Buy)
        {
            var position = Position.Open(
                order.StrategyId, order.Symbol, order.Side,
                order.ExecutedPrice!, order.FilledQuantity!);

            await _positionRepository.AddAsync(position, cancellationToken);
        }
        else
        {
            // Buscar posición abierta para cerrarla
            var openPositions = await _positionRepository
                .GetOpenByStrategyIdAsync(order.StrategyId, cancellationToken);

            var position = openPositions
                .FirstOrDefault(p => p.Symbol == order.Symbol && p.Side == OrderSide.Buy);

            if (position is not null && order.ExecutedPrice is not null)
            {
                position.Close(order.ExecutedPrice);
                await _positionRepository.UpdateAsync(position, cancellationToken);
            }
        }
    }
}
