using TradingBot.Core.Enums;

namespace TradingBot.Application.RiskManagement;

/// <summary>
/// Calcula el impacto de fees y slippage en precio y cantidad ejecutados.
/// Usado tanto en Paper Trading como en BacktestEngine.
/// </summary>
internal static class FeeAndSlippageCalculator
{
    /// <summary>
    /// Calcula el precio ajustado por slippage para una orden de mercado.
    /// Buy: precio sube. Sell: precio baja.
    /// </summary>
    public static decimal ApplySlippage(decimal price, OrderSide side, decimal slippagePercent)
    {
        if (slippagePercent <= 0m) return price;

        return side == OrderSide.Buy
            ? price * (1m + slippagePercent)
            : price * (1m - slippagePercent);
    }

    /// <summary>
    /// Calcula la comisión en USDT (o quote asset) para un trade.
    /// </summary>
    public static decimal CalculateFee(decimal executedPrice, decimal quantity, decimal feePercent)
    {
        return executedPrice * quantity * feePercent;
    }

    /// <summary>
    /// Calcula la cantidad efectiva recibida después de descontar comisión.
    /// Para Buy: recibe menos del asset base. Para Sell: recibe menos USDT.
    /// </summary>
    public static decimal QuantityAfterFee(decimal quantity, decimal feePercent)
    {
        return quantity * (1m - feePercent);
    }

    /// <summary>
    /// Calcula P&amp;L neto de un trade redondo (entry + exit) incluyendo fees y slippage.
    /// </summary>
    public static TradeImpact CalculateRoundTripImpact(
        OrderSide side,
        decimal entryPrice,
        decimal exitPrice,
        decimal quantity,
        decimal feePercent,
        decimal slippagePercent,
        bool isMarketOrder = true)
    {
        // Slippage solo en market orders
        var slippage = isMarketOrder ? slippagePercent : 0m;

        var adjustedEntry = ApplySlippage(entryPrice, side, slippage);
        var exitSide = side == OrderSide.Buy ? OrderSide.Sell : OrderSide.Buy;
        var adjustedExit = ApplySlippage(exitPrice, exitSide, slippage);

        var entryFee = CalculateFee(adjustedEntry, quantity, feePercent);
        var exitFee  = CalculateFee(adjustedExit, quantity, feePercent);
        var totalFees = entryFee + exitFee;

        var grossPnL = side == OrderSide.Buy
            ? (adjustedExit - adjustedEntry) * quantity
            : (adjustedEntry - adjustedExit) * quantity;

        var totalSlippageCost = side == OrderSide.Buy
            ? ((adjustedEntry - entryPrice) + (exitPrice - adjustedExit)) * quantity
            : ((entryPrice - adjustedEntry) + (adjustedExit - exitPrice)) * quantity;

        var netPnL = grossPnL - totalFees;

        return new TradeImpact(
            AdjustedEntryPrice: adjustedEntry,
            AdjustedExitPrice: adjustedExit,
            GrossPnL: grossPnL,
            TotalFees: totalFees,
            TotalSlippageCost: Math.Abs(totalSlippageCost),
            NetPnL: netPnL);
    }
}

/// <summary>Resultado del cálculo de impacto de un trade con fees y slippage.</summary>
internal sealed record TradeImpact(
    decimal AdjustedEntryPrice,
    decimal AdjustedExitPrice,
    decimal GrossPnL,
    decimal TotalFees,
    decimal TotalSlippageCost,
    decimal NetPnL);
