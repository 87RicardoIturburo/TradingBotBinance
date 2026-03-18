namespace TradingBot.Application.RiskManagement;

/// <summary>
/// Configuración global de riesgo. Se aplica como límite absoluto sobre TODAS
/// las estrategias combinadas. Configurable en <c>appsettings.json</c> sección <c>GlobalRisk</c>.
/// </summary>
public sealed class GlobalRiskSettings
{
    public const string SectionName = "GlobalRisk";

    /// <summary>Pérdida diaria máxima global (suma de todas las estrategias) en USDT. 0 = deshabilitado.</summary>
    public decimal MaxDailyLossUsdt { get; set; }

    /// <summary>Máximo de posiciones abiertas globales (todas las estrategias). 0 = deshabilitado.</summary>
    public int MaxGlobalOpenPositions { get; set; }

    /// <summary>Exposición máxima Long del portafolio en USDT. 0 = deshabilitado.</summary>
    public decimal MaxPortfolioLongExposureUsdt { get; set; }

    /// <summary>
    /// Exposición máxima Short del portafolio en USDT. 0 = deshabilitado.
    /// NOTA: En modo Spot solo se opera Long. Esta propiedad está reservada
    /// para futura implementación de Margin Trading / Futures. No eliminar.
    /// </summary>
    public decimal MaxPortfolioShortExposureUsdt { get; set; }

    /// <summary>Porcentaje máximo de exposición en un solo símbolo sobre el total del portafolio (0-100). 0 = deshabilitado.</summary>
    public decimal MaxExposurePerSymbolPercent { get; set; }

    /// <summary>
    /// Porcentaje máximo de drawdown diario de la cuenta antes de suspender todas las estrategias (0-100). 0 = deshabilitado.
    /// Se calcula sobre el balance al inicio del día.
    /// </summary>
    public decimal MaxAccountDrawdownPercent { get; set; }
}
