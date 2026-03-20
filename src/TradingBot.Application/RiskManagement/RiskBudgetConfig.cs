namespace TradingBot.Application.RiskManagement;

/// <summary>
/// Configuración del presupuesto de riesgo global.
/// Sección <c>RiskBudget</c> en <c>appsettings.json</c>.
/// </summary>
public sealed class RiskBudgetConfig
{
    public const string SectionName = "RiskBudget";

    /// <summary>Capital total asignado al bot en USDT. 0 = guardián deshabilitado.</summary>
    public decimal TotalCapitalUsdt { get; set; }

    /// <summary>Porcentaje máximo de pérdida sobre el capital total (ej: 10 = 10%). Default: 10.</summary>
    public decimal MaxLossPercent { get; set; } = 10m;

    /// <summary>
    /// Umbral (% del max loss) para pasar a <see cref="Core.Enums.RiskLevel.Reduced"/>.
    /// Default: 30 (cuando se pierde el 30% del presupuesto máximo).
    /// </summary>
    public decimal ReducedThresholdPercent { get; set; } = 30m;

    /// <summary>
    /// Umbral (% del max loss) para pasar a <see cref="Core.Enums.RiskLevel.Critical"/>.
    /// Default: 60.
    /// </summary>
    public decimal CriticalThresholdPercent { get; set; } = 60m;

    /// <summary>
    /// Umbral (% del max loss) para pasar a <see cref="Core.Enums.RiskLevel.CloseOnly"/>.
    /// Default: 80.
    /// </summary>
    public decimal CloseOnlyThresholdPercent { get; set; } = 80m;

    /// <summary>
    /// Multiplicador de <c>MaxOrderAmountUsdt</c> en nivel Reduced. Default: 0.7 (70% del original).
    /// </summary>
    public decimal ReducedMultiplier { get; set; } = 0.7m;

    /// <summary>
    /// Multiplicador de <c>MaxOrderAmountUsdt</c> en nivel Critical. Default: 0.4 (40% del original).
    /// </summary>
    public decimal CriticalMultiplier { get; set; } = 0.4m;

    /// <summary>
    /// Máximo de posiciones abiertas globales en nivel Critical. Default: 1.
    /// </summary>
    public int CriticalMaxOpenPositions { get; set; } = 1;

    /// <summary>Pérdida máxima permitida calculada: TotalCapitalUsdt × MaxLossPercent / 100.</summary>
    public decimal MaxLossUsdt => TotalCapitalUsdt * MaxLossPercent / 100m;

    /// <summary>Indica si el guardián está habilitado (capital > 0).</summary>
    public bool IsEnabled => TotalCapitalUsdt > 0;
}
