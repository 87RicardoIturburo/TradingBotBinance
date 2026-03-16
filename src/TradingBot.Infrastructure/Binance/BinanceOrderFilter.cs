using TradingBot.Core.Common;
using TradingBot.Core.ValueObjects;

namespace TradingBot.Infrastructure.Binance;

/// <summary>
/// Ajusta cantidad y precio de una orden para cumplir los filtros del exchange de Binance.
/// Delega la lógica de ajuste a <see cref="ExchangeSymbolFilters"/> (Core).
/// </summary>
internal static class BinanceOrderFilter
{
    /// <summary>
    /// Ajusta la cantidad al stepSize más cercano hacia abajo (floor).
    /// </summary>
    public static decimal AdjustQuantity(decimal quantity, decimal stepSize)
    {
        if (stepSize <= 0) return quantity;
        return Math.Floor(quantity / stepSize) * stepSize;
    }

    /// <summary>
    /// Ajusta el precio al tickSize más cercano (round half-up).
    /// </summary>
    public static decimal AdjustPrice(decimal price, decimal tickSize)
    {
        if (tickSize <= 0) return price;
        return Math.Round(price / tickSize, MidpointRounding.AwayFromZero) * tickSize;
    }

    /// <summary>
    /// Valida y ajusta cantidad y precio contra los filtros del símbolo.
    /// Delega a <see cref="ExchangeSymbolFilters.ValidateAndAdjust"/>.
    /// </summary>
    public static Result<(decimal Quantity, decimal? Price), DomainError> ValidateAndAdjust(
        decimal            quantity,
        decimal?           limitPrice,
        ExchangeSymbolFilters filters)
    {
        ArgumentNullException.ThrowIfNull(filters);
        return filters.ValidateAndAdjust(quantity, limitPrice);
    }
}
