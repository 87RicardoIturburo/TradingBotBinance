namespace TradingBot.Application.AutoPilot;

/// <summary>
/// Configuración del pool dinámico de símbolos (AutoPilot v2). Hot-reloadable vía appsettings.json.
/// <c>Enabled</c> en config es solo el default de arranque; en runtime lo controla
/// <see cref="TradingBot.Core.Interfaces.Services.ISymbolPool.SetEnabledAsync"/>.
/// </summary>
public sealed class SymbolPoolConfig
{
    public const string SectionName = "SymbolPool";

    public bool Enabled { get; set; }
    public int ObservedPoolSize { get; set; } = 30;
    public int ActiveTopK { get; set; } = 5;
    public int MaxConcurrentRunners { get; set; } = 40;
    public int EvaluationIntervalSeconds { get; set; } = 90;

    public string? BaseTemplateId { get; set; }
    public string DefaultTimeframe { get; set; } = "15m";
    public string DefaultTradingMode { get; set; } = "PaperTrading";

    // ── Pesos normalizados (deben sumar 1.0) ──────────────────────────
    public decimal RegimeClarityWeight { get; set; } = 0.25m;
    public decimal AdxStrengthWeight { get; set; } = 0.20m;
    public decimal RelativeVolumeWeight { get; set; } = 0.15m;
    public decimal AtrHealthWeight { get; set; } = 0.125m;
    public decimal BandWidthWeight { get; set; } = 0.125m;
    public decimal SignalProximityWeight { get; set; } = 0.15m;

    // ── Histéresis ────────────────────────────────────────────────────
    public int MinCyclesInTopK { get; set; } = 2;
    public int MinCyclesOutOfTopK { get; set; } = 2;
    public int MinTimeInTopKBeforeEntrySeconds { get; set; } = 120;

    // ── Zombie cleanup ────────────────────────────────────────────────
    public int IdleTimeoutMinutes { get; set; } = 15;
    public decimal ZombieScoreThreshold { get; set; } = 20m;

    // ── Umbral mínimo de score ────────────────────────────────────────
    public decimal MinTradabilityScore { get; set; } = 40m;

    // ── Exclusión temporal ────────────────────────────────────────────
    public int ExclusionDurationMinutes { get; set; } = 30;
}
