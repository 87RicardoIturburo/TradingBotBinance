using TradingBot.Core.Common;
using TradingBot.Core.Enums;
using TradingBot.Core.ValueObjects;

namespace TradingBot.Core.Entities;

/// <summary>
/// Posición abierta o cerrada de un activo. Aggregate root con ciclo de vida propio.
/// Se crea al ejecutarse una orden de entrada y se cierra con una orden de salida.
/// </summary>
public sealed class Position : AggregateRoot<Guid>
{
    public Guid           StrategyId   { get; private set; }
    public Symbol         Symbol       { get; private set; } = null!;
    public OrderSide      Side         { get; private set; }
    public Price          EntryPrice   { get; private set; } = null!;
    public Price          CurrentPrice { get; private set; } = null!;
    /// <summary>Precio más alto alcanzado desde la apertura (Long trailing stop).</summary>
    public Price          HighestPriceSinceEntry { get; private set; } = null!;
    /// <summary>
    /// Precio más bajo alcanzado desde la apertura (Short trailing stop).
    /// NOTA: En modo Spot solo se opera Long. Esta propiedad está reservada
    /// para futura implementación de Margin Trading / Futures. No eliminar.
    /// </summary>
    public Price          LowestPriceSinceEntry  { get; private set; } = null!;
    public Quantity       Quantity     { get; private set; } = null!;
    public bool           IsOpen       { get; private set; }
    public decimal?       RealizedPnL  { get; private set; }
    /// <summary>Comisión pagada al abrir la posición (en quote asset).</summary>
    public decimal        EntryFee     { get; private set; }
    /// <summary>Comisión pagada al cerrar la posición (en quote asset).</summary>
    public decimal        ExitFee      { get; private set; }
    /// <summary>Motivo por el que se cerró la posición. <c>null</c> mientras está abierta.</summary>
    public CloseReason?   CloseReason  { get; private set; }
    public DateTimeOffset OpenedAt     { get; private set; }
    public DateTimeOffset? ClosedAt    { get; private set; }

    /// <summary>
    /// P&amp;L no realizado calculado sobre <see cref="CurrentPrice"/>, descontando fees.
    /// Incluye <see cref="EntryFee"/> ya pagada y una estimación de exit fee igual a <see cref="EntryFee"/>.
    /// </summary>
    public decimal UnrealizedPnL => IsOpen
        ? (Side == OrderSide.Buy
            ? (CurrentPrice.Value - EntryPrice.Value) * Quantity.Value
            : (EntryPrice.Value - CurrentPrice.Value) * Quantity.Value)
          - EntryFee - EntryFee // Estimar exit fee igual a entry fee
        : 0m;

    /// <summary>Porcentaje de retorno no realizado sobre el capital invertido.</summary>
    public decimal UnrealizedPnLPercent =>
        EntryPrice.Value == 0m ? 0m
        : UnrealizedPnL / (EntryPrice.Value * Quantity.Value) * 100m;

    private Position(Guid id) : base(id) { }
    private Position() : base(Guid.Empty) { } // EF Core

    public static Position Open(
        Guid       strategyId,
        Symbol     symbol,
        OrderSide  side,
        Price      entryPrice,
        Quantity   quantity,
        decimal    entryFee = 0m)
    {
        var now = DateTimeOffset.UtcNow;
        return new Position(Guid.NewGuid())
        {
            StrategyId   = strategyId,
            Symbol       = symbol,
            Side         = side,
            EntryPrice             = entryPrice,
            CurrentPrice           = entryPrice,
            HighestPriceSinceEntry = entryPrice,
            LowestPriceSinceEntry  = entryPrice,
            Quantity               = quantity,
            EntryFee               = entryFee,
            IsOpen                 = true,
            OpenedAt               = now
        };
    }

    /// <summary>Actualiza el precio de mercado, recalcula P&amp;L y rastrea máximos/mínimos para trailing stop.</summary>
    public void UpdatePrice(Price currentPrice)
    {
        if (!IsOpen) return;
        CurrentPrice = currentPrice;

        if (currentPrice.Value > HighestPriceSinceEntry.Value)
            HighestPriceSinceEntry = currentPrice;
        if (currentPrice.Value < LowestPriceSinceEntry.Value)
            LowestPriceSinceEntry = currentPrice;
    }

    /// <summary>
    /// Actualiza la cantidad llenada y el precio promedio ponderado tras un partial fill.
    /// El nuevo precio de entrada es el promedio ponderado: <c>(oldQty × oldPrice + addedQty × fillPrice) / newQty</c>.
    /// </summary>
    public void AccumulatePartialFill(Quantity totalFilledQuantity, Price averageFillPrice, decimal additionalFee)
    {
        if (!IsOpen) return;

        Quantity  = totalFilledQuantity;
        EntryPrice = averageFillPrice;
        EntryFee  += additionalFee;
        Version++;
    }

    /// <summary>Cierra la posición al precio indicado y devuelve el P&amp;L realizado neto de fees.</summary>
    public Result<decimal, DomainError> Close(Price closePrice, decimal exitFee = 0m, CloseReason closeReason = Enums.CloseReason.Manual)
    {
        if (!IsOpen)
            return Result<decimal, DomainError>.Failure(
                DomainError.InvalidOperation("La posición ya está cerrada."));

        CurrentPrice = closePrice;
        ExitFee      = exitFee;
        CloseReason  = closeReason;

        var grossPnL = Side == OrderSide.Buy
            ? (closePrice.Value - EntryPrice.Value) * Quantity.Value
            : (EntryPrice.Value - closePrice.Value) * Quantity.Value;

        RealizedPnL = grossPnL - EntryFee - ExitFee;

        IsOpen   = false;
        ClosedAt = DateTimeOffset.UtcNow;
        Version++;

        return Result<decimal, DomainError>.Success(RealizedPnL.Value);
    }
}
