namespace TradingBot.Core.ValueObjects;

/// <summary>
/// Explicación estructurada de por qué se ejecutó un trade.
/// Captura el contexto completo de indicadores, confirmaciones, régimen y riesgo
/// en el momento exacto de la decisión.
/// </summary>
public sealed record TradeExplanation
{
    public string SignalSource { get; init; } = string.Empty;
    public string Direction { get; init; } = string.Empty;
    public decimal EntryPrice { get; init; }
    public string MarketRegime { get; init; } = string.Empty;
    public decimal? AdxValue { get; init; }
    public bool? AdxBullish { get; init; }
    public string IndicatorSnapshot { get; init; } = string.Empty;
    public int ConfirmationsObtained { get; init; }
    public int ConfirmationsTotal { get; init; }
    public IReadOnlyList<string> ConfirmationDetails { get; init; } = [];
    public IReadOnlyList<string> FiltersPassed { get; init; } = [];
    public string? RiskCheckSummary { get; init; }
    public string? ExitReason { get; init; }
    public decimal? ExitPrice { get; init; }
    public decimal? RealizedPnL { get; init; }
    public decimal? DurationMinutes { get; init; }
    public DateTimeOffset Timestamp { get; init; }
}
