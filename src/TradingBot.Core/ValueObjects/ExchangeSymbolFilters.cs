using TradingBot.Core.Common;

namespace TradingBot.Core.ValueObjects;

/// <summary>
/// Filtros de exchange para un símbolo específico, obtenidos de Binance Exchange Info.
/// Se usan para ajustar cantidad y precio antes de enviar una orden al exchange.
/// </summary>
public sealed record ExchangeSymbolFilters(
    string  Symbol,
    // LOT_SIZE
    decimal MinQty,
    decimal MaxQty,
    decimal StepSize,
    // PRICE_FILTER
    decimal TickSize,
    // MIN_NOTIONAL (o NOTIONAL)
    decimal MinNotional,
    // MAX_NUM_ORDERS
    int     MaxNumOrders)
{
    /// <summary>Número de decimales que admite la cantidad según el stepSize.</summary>
    public int QuantityPrecision =>
        StepSize == 0 ? 8 : Math.Max(0, (int)Math.Ceiling(-Math.Log10((double)StepSize)));

    /// <summary>Número de decimales que admite el precio según el tickSize.</summary>
    public int PricePrecision =>
        TickSize == 0 ? 8 : Math.Max(0, (int)Math.Ceiling(-Math.Log10((double)TickSize)));

    /// <summary>
    /// Ajusta la cantidad al stepSize más cercano hacia abajo (floor).
    /// Binance rechaza cantidades que no sean múltiplos exactos del stepSize.
    /// </summary>
    public decimal AdjustQuantity(decimal quantity)
    {
        if (StepSize <= 0) return quantity;
        return Math.Floor(quantity / StepSize) * StepSize;
    }

    /// <summary>
    /// Ajusta el precio al tickSize más cercano (round half-up).
    /// Binance rechaza precios que no sean múltiplos exactos del tickSize.
    /// </summary>
    public decimal AdjustPrice(decimal price)
    {
        if (TickSize <= 0) return price;
        return Math.Round(price / TickSize, MidpointRounding.AwayFromZero) * TickSize;
    }

    /// <summary>
    /// Valida y ajusta cantidad y precio contra los filtros del símbolo.
    /// Devuelve error si la cantidad ajustada es inválida o el notional no alcanza el mínimo.
    /// </summary>
    public Result<(decimal Quantity, decimal? Price), DomainError> ValidateAndAdjust(
        decimal  quantity,
        decimal? limitPrice)
    {
        var adjustedQty = AdjustQuantity(quantity);

        if (adjustedQty <= 0)
            return Result<(decimal, decimal?), DomainError>.Failure(
                DomainError.Validation(
                    $"La cantidad ajustada es cero o negativa para {Symbol} " +
                    $"(stepSize: {StepSize})."));

        if (adjustedQty < MinQty)
            return Result<(decimal, decimal?), DomainError>.Failure(
                DomainError.Validation(
                    $"Cantidad {adjustedQty} < mínimo permitido {MinQty} para {Symbol}."));

        if (MaxQty > 0 && adjustedQty > MaxQty)
            return Result<(decimal, decimal?), DomainError>.Failure(
                DomainError.Validation(
                    $"Cantidad {adjustedQty} > máximo permitido {MaxQty} para {Symbol}."));

        decimal? adjustedPrice = limitPrice.HasValue
            ? AdjustPrice(limitPrice.Value)
            : null;

        // Validación MIN_NOTIONAL: solo aplica si tenemos precio
        if (MinNotional > 0 && adjustedPrice.HasValue)
        {
            var notional = adjustedQty * adjustedPrice.Value;
            if (notional < MinNotional)
                return Result<(decimal, decimal?), DomainError>.Failure(
                    DomainError.Validation(
                        $"Notional {notional:F2} USDT < mínimo requerido {MinNotional:F2} USDT " +
                        $"para {Symbol}."));
        }

        return Result<(decimal, decimal?), DomainError>.Success((adjustedQty, adjustedPrice));
    }
}
