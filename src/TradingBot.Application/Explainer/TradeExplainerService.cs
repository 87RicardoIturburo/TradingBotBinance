using TradingBot.Core.Enums;
using TradingBot.Core.ValueObjects;

namespace TradingBot.Application.Explainer;

/// <summary>
/// Construye explicaciones estructuradas de por qué se ejecutó un trade.
/// Captura indicadores, confirmaciones, régimen y filtros al momento de la decisión.
/// </summary>
internal static class TradeExplainerService
{
    public static TradeExplanation BuildEntryExplanation(
        string signalSource,
        OrderSide direction,
        decimal entryPrice,
        string marketRegime,
        decimal? adxValue,
        bool? adxBullish,
        string indicatorSnapshot,
        int confirmationsObtained,
        int confirmationsTotal,
        IReadOnlyList<string> confirmationDetails,
        IReadOnlyList<string> filtersPassed,
        string? riskCheckSummary)
    {
        return new TradeExplanation
        {
            SignalSource = signalSource,
            Direction = direction.ToString(),
            EntryPrice = entryPrice,
            MarketRegime = marketRegime,
            AdxValue = adxValue,
            AdxBullish = adxBullish,
            IndicatorSnapshot = indicatorSnapshot,
            ConfirmationsObtained = confirmationsObtained,
            ConfirmationsTotal = confirmationsTotal,
            ConfirmationDetails = confirmationDetails,
            FiltersPassed = filtersPassed,
            RiskCheckSummary = riskCheckSummary,
            Timestamp = DateTimeOffset.UtcNow
        };
    }

    public static TradeExplanation BuildExitExplanation(
        string exitReason,
        decimal exitPrice,
        decimal? realizedPnL,
        double? durationMinutes,
        string marketRegime,
        string indicatorSnapshot)
    {
        return new TradeExplanation
        {
            SignalSource = "Exit",
            Direction = OrderSide.Sell.ToString(),
            EntryPrice = exitPrice,
            ExitReason = exitReason,
            ExitPrice = exitPrice,
            RealizedPnL = realizedPnL,
            DurationMinutes = durationMinutes.HasValue ? (decimal)durationMinutes.Value : null,
            MarketRegime = marketRegime,
            IndicatorSnapshot = indicatorSnapshot,
            Timestamp = DateTimeOffset.UtcNow
        };
    }

    public static IReadOnlyList<string> BuildConfirmationDetails(
        string indicatorSnapshot,
        int confirms,
        int total)
    {
        var details = new List<string>();

        if (string.IsNullOrWhiteSpace(indicatorSnapshot))
            return details;

        var parts = indicatorSnapshot.Split(" | ", StringSplitOptions.RemoveEmptyEntries);
        foreach (var part in parts)
        {
            var trimmed = part.Trim();
            if (trimmed.StartsWith("Confirm=", StringComparison.Ordinal) ||
                trimmed.StartsWith("Regime=", StringComparison.Ordinal))
                continue;

            details.Add(trimmed);
        }

        return details;
    }

    public static IReadOnlyList<string> BuildFiltersList(
        bool htfAligned,
        bool btcAligned,
        bool cooldownPassed)
    {
        var filters = new List<string>();

        if (htfAligned)
            filters.Add("HTF EMA confirmada");
        if (btcAligned)
            filters.Add("BTC correlación alineada");
        if (cooldownPassed)
            filters.Add("Cooldown respetado");

        return filters;
    }
}
