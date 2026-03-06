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
    public Quantity       Quantity     { get; private set; } = null!;
    public bool           IsOpen       { get; private set; }
    public decimal?       RealizedPnL  { get; private set; }
    public DateTimeOffset OpenedAt     { get; private set; }
    public DateTimeOffset? ClosedAt    { get; private set; }

    /// <summary>
    /// P&amp;L no realizado calculado sobre <see cref="CurrentPrice"/>.
    /// Long:  (Current - Entry) * Qty
    /// Short: (Entry - Current) * Qty
    /// </summary>
    public decimal UnrealizedPnL => IsOpen
        ? Side == OrderSide.Buy
            ? (CurrentPrice.Value - EntryPrice.Value) * Quantity.Value
            : (EntryPrice.Value - CurrentPrice.Value) * Quantity.Value
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
        Quantity   quantity)
    {
        var now = DateTimeOffset.UtcNow;
        return new Position(Guid.NewGuid())
        {
            StrategyId   = strategyId,
            Symbol       = symbol,
            Side         = side,
            EntryPrice   = entryPrice,
            CurrentPrice = entryPrice,
            Quantity     = quantity,
            IsOpen       = true,
            OpenedAt     = now
        };
    }

    /// <summary>Actualiza el precio de mercado y recalcula el P&amp;L no realizado.</summary>
    public void UpdatePrice(Price currentPrice)
    {
        if (!IsOpen) return;
        CurrentPrice = currentPrice;
    }

    /// <summary>Cierra la posición al precio indicado y devuelve el P&amp;L realizado.</summary>
    public Result<decimal, DomainError> Close(Price closePrice)
    {
        if (!IsOpen)
            return Result<decimal, DomainError>.Failure(
                DomainError.InvalidOperation("La posición ya está cerrada."));

        CurrentPrice = closePrice;
        RealizedPnL  = Side == OrderSide.Buy
            ? (closePrice.Value - EntryPrice.Value) * Quantity.Value
            : (EntryPrice.Value - closePrice.Value) * Quantity.Value;

        IsOpen   = false;
        ClosedAt = DateTimeOffset.UtcNow;
        Version++;

        return Result<decimal, DomainError>.Success(RealizedPnL.Value);
    }
}
