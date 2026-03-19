namespace TradingBot.Application.RiskManagement;

/// <summary>
/// Calcula el tamaño óptimo de posición basándose en ATR (volatilidad).
/// <para>
/// Fórmula: <c>positionSize = riskAmount / stopDistance</c>
/// donde <c>riskAmount = balance × riskPercentPerTrade</c>
/// y <c>stopDistance = ATR × atrMultiplier</c>.
/// </para>
/// En mercados volátiles (ATR alto), el position size se reduce automáticamente.
/// En mercados tranquilos (ATR bajo), se permite una posición más grande.
/// </summary>
internal static class PositionSizer
{
    /// <summary>
    /// Calcula la cantidad en USDT óptima para una posición.
    /// </summary>
    /// <param name="accountBalanceUsdt">Balance disponible en USDT.</param>
    /// <param name="riskPercentPerTrade">Porcentaje del balance a arriesgar por trade (ej: 0.01 = 1%).</param>
    /// <param name="atrValue">Valor actual del ATR en unidades de precio.</param>
    /// <param name="atrMultiplier">Multiplicador del ATR para la distancia de stop (ej: 2.0).</param>
    /// <param name="currentPrice">Precio actual del activo.</param>
    /// <param name="maxOrderAmountUsdt">Monto máximo permitido por orden (cap de seguridad).</param>
    /// <returns>Resultado con el monto en USDT y la distancia de stop-loss.</returns>
    public static PositionSizeResult Calculate(
        decimal accountBalanceUsdt,
        decimal riskPercentPerTrade,
        decimal atrValue,
        decimal atrMultiplier,
        decimal currentPrice,
        decimal maxOrderAmountUsdt)
    {
        if (atrValue <= 0 || currentPrice <= 0 || accountBalanceUsdt <= 0)
        {
            // DESIGN-4 fix: fallback conservador al 50% del máximo cuando ATR no está disponible.
            // Evita exponer el máximo permitido en condiciones de incertidumbre.
            var fallbackAmount = maxOrderAmountUsdt * 0.5m;
            return new PositionSizeResult(
                AmountUsdt: fallbackAmount,
                StopDistancePrice: 0,
                QuantityBaseAsset: fallbackAmount / (currentPrice > 0 ? currentPrice : 1),
                WasAtrCalculated: false);
        }

        var riskAmount = accountBalanceUsdt * riskPercentPerTrade;
        var stopDistance = atrValue * atrMultiplier;

        // Cantidad del activo base: cuánto comprar para que si cae stopDistance, pierda riskAmount
        var quantityBase = riskAmount / stopDistance;
        var amountUsdt = quantityBase * currentPrice;

        // Cap de seguridad: nunca superar maxOrderAmountUsdt
        if (amountUsdt > maxOrderAmountUsdt)
        {
            amountUsdt = maxOrderAmountUsdt;
            quantityBase = amountUsdt / currentPrice;
        }

        // Mínimo viable: Binance MIN_NOTIONAL requiere al menos $10 por orden
        if (amountUsdt < 10m)
            amountUsdt = 10m;

        return new PositionSizeResult(
            AmountUsdt: amountUsdt,
            StopDistancePrice: stopDistance,
            QuantityBaseAsset: quantityBase,
            WasAtrCalculated: true);
    }
}

/// <summary>Resultado del cálculo de position sizing.</summary>
internal sealed record PositionSizeResult(
    /// <summary>Monto en USDT a invertir.</summary>
    decimal AmountUsdt,
    /// <summary>Distancia de stop-loss en precio absoluto (ATR × multiplier).</summary>
    decimal StopDistancePrice,
    /// <summary>Cantidad del activo base a comprar/vender.</summary>
    decimal QuantityBaseAsset,
    /// <summary><c>true</c> si se usó ATR para el cálculo; <c>false</c> si se usó el monto fijo como fallback.</summary>
    bool WasAtrCalculated);
