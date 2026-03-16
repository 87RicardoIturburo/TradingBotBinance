using TradingBot.Core.Common;
using TradingBot.Core.Enums;
using TradingBot.Core.Events;
using TradingBot.Core.ValueObjects;

namespace TradingBot.Core.Entities;

/// <summary>
/// Orden de compra/venta. Aggregate root con ciclo de vida propio.
/// Soporta modos Live, Testnet y Paper Trading sin cambiar la lógica.
/// </summary>
public sealed class Order : AggregateRoot<Guid>
{
    public Guid           StrategyId     { get; private set; }
    public Symbol         Symbol         { get; private set; } = null!;
    public OrderSide      Side           { get; private set; }
    public OrderType      Type           { get; private set; }
    public Quantity       Quantity       { get; private set; } = null!;
    public Price?         LimitPrice     { get; private set; }
    public Price?         StopPrice      { get; private set; }
    public Price?         EstimatedPrice { get; private set; }
    public Quantity?      FilledQuantity { get; private set; }
    public Price?         ExecutedPrice  { get; private set; }
    public OrderStatus    Status         { get; private set; }
    public TradingMode    Mode           { get; private set; }
    public string?        BinanceOrderId { get; private set; }
    /// <summary>Comisión cobrada por el exchange (o simulada en paper trading) en quote asset.</summary>
    public decimal        Fee            { get; private set; }
    public DateTimeOffset CreatedAt      { get; private set; }
    public DateTimeOffset UpdatedAt      { get; private set; }
    public DateTimeOffset? FilledAt      { get; private set; }

    public bool IsPaperTrade => Mode == TradingMode.PaperTrading;
    public bool IsTerminal   => Status is OrderStatus.Filled
                                       or OrderStatus.Cancelled
                                       or OrderStatus.Rejected
                                       or OrderStatus.Expired;

    /// <summary>
    /// Valor notional estimado de la orden en USDT.
    /// Usa LimitPrice para Limit orders, EstimatedPrice para Market orders pre-ejecución,
    /// o ExecutedPrice para órdenes ya ejecutadas.
    /// </summary>
    public decimal NotionalValue => Quantity.Value *
        (LimitPrice?.Value ?? EstimatedPrice?.Value ?? ExecutedPrice?.Value ?? 0m);

    private Order(Guid id) : base(id) { }
    private Order() : base(Guid.Empty) { } // EF Core

    public static Result<Order, DomainError> Create(
        Guid       strategyId,
        Symbol     symbol,
        OrderSide  side,
        OrderType  type,
        Quantity   quantity,
        TradingMode mode,
        Price?     limitPrice = null,
        Price?     stopPrice  = null,
        Price?     estimatedPrice = null)
    {
        if (type == OrderType.Limit && limitPrice is null)
            return Result<Order, DomainError>.Failure(
                DomainError.Validation("Una orden Limit requiere un precio límite."));

        if (type == OrderType.StopLimit && (limitPrice is null || stopPrice is null))
            return Result<Order, DomainError>.Failure(
                DomainError.Validation("Una orden Stop-Limit requiere precio límite y precio stop."));

        var now = DateTimeOffset.UtcNow;
        return Result<Order, DomainError>.Success(new Order(Guid.NewGuid())
        {
            StrategyId     = strategyId,
            Symbol         = symbol,
            Side           = side,
            Type           = type,
            Quantity       = quantity,
            Mode           = mode,
            LimitPrice     = limitPrice,
            StopPrice      = stopPrice,
            EstimatedPrice = estimatedPrice,
            Status         = OrderStatus.Pending,
            CreatedAt      = now,
            UpdatedAt      = now
        });
    }

    /// <summary>
    /// Ajusta cantidad y/o precio para cumplir con los filtros del exchange (LOT_SIZE, PRICE_FILTER).
    /// Solo debe llamarse ANTES de la ejecución.
    /// </summary>
    public void AdjustForExchange(decimal adjustedQuantity, decimal? adjustedPrice)
    {
        var newQty = Quantity.Create(adjustedQuantity);
        if (newQty.IsSuccess)
            Quantity = newQty.Value;

        if (adjustedPrice.HasValue)
        {
            var newPrice = Price.Create(adjustedPrice.Value);
            if (newPrice.IsSuccess)
                LimitPrice = newPrice.Value;
        }
    }

    /// <summary>Envía la orden al exchange (o la registra en paper trading).</summary>
    public Result<Order, DomainError> Submit(string? binanceOrderId = null)
    {
        if (Status != OrderStatus.Pending)
            return Result<Order, DomainError>.Failure(
                DomainError.InvalidOperation($"No se puede enviar una orden en estado '{Status}'."));

        BinanceOrderId = binanceOrderId;
        Status         = OrderStatus.Submitted;
        UpdatedAt      = DateTimeOffset.UtcNow;
        Version++;

        RaiseDomainEvent(new OrderPlacedEvent(
            Id, StrategyId, Symbol, Side, Type, Quantity, LimitPrice, IsPaperTrade));

        return Result<Order, DomainError>.Success(this);
    }

    /// <summary>Registra un llenado parcial sin cerrar la orden.</summary>
    public Result<Order, DomainError> PartialFill(Quantity filledQuantity, Price executedPrice)
    {
        if (Status is not (OrderStatus.Submitted or OrderStatus.PartiallyFilled))
            return Result<Order, DomainError>.Failure(
                DomainError.InvalidOperation($"No se puede llenar parcialmente una orden en estado '{Status}'."));

        FilledQuantity = filledQuantity;
        ExecutedPrice  = executedPrice;
        Status         = OrderStatus.PartiallyFilled;
        UpdatedAt      = DateTimeOffset.UtcNow;
        Version++;

        return Result<Order, DomainError>.Success(this);
    }

    /// <summary>Marca la orden como completamente ejecutada.</summary>
    public Result<Order, DomainError> Fill(Quantity filledQuantity, Price executedPrice, decimal fee = 0m)
    {
        if (Status is not (OrderStatus.Submitted or OrderStatus.PartiallyFilled))
            return Result<Order, DomainError>.Failure(
                DomainError.InvalidOperation($"No se puede completar una orden en estado '{Status}'."));

        FilledQuantity = filledQuantity;
        ExecutedPrice  = executedPrice;
        Fee            = fee;
        Status         = OrderStatus.Filled;
        FilledAt       = DateTimeOffset.UtcNow;
        UpdatedAt      = FilledAt.Value;
        Version++;

        RaiseDomainEvent(new OrderFilledEvent(
            Id, StrategyId, Symbol, Side, filledQuantity, executedPrice, IsPaperTrade));

        return Result<Order, DomainError>.Success(this);
    }

    /// <summary>Cancela la orden. Solo posible en estados no terminales.</summary>
    public Result<Order, DomainError> Cancel(string reason)
    {
        if (IsTerminal)
            return Result<Order, DomainError>.Failure(
                DomainError.InvalidOperation($"No se puede cancelar una orden en estado terminal '{Status}'."));

        Status    = OrderStatus.Cancelled;
        UpdatedAt = DateTimeOffset.UtcNow;
        Version++;

        RaiseDomainEvent(new OrderCancelledEvent(Id, StrategyId, reason));

        return Result<Order, DomainError>.Success(this);
    }

    /// <summary>El exchange rechazó la orden (fondos insuficientes, rate limit, etc.).</summary>
    public Result<Order, DomainError> Reject(string reason)
    {
        if (Status != OrderStatus.Submitted)
            return Result<Order, DomainError>.Failure(
                DomainError.InvalidOperation("Solo se puede rechazar una orden en estado 'Submitted'."));

        Status    = OrderStatus.Rejected;
        UpdatedAt = DateTimeOffset.UtcNow;
        Version++;

        RaiseDomainEvent(new OrderCancelledEvent(Id, StrategyId, $"Rechazada por el exchange: {reason}"));

        return Result<Order, DomainError>.Success(this);
    }
}
