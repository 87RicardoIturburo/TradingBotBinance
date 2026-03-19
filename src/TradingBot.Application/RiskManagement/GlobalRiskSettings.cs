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

    /// <summary>
    /// Mínimo de trades cerrados necesarios para evaluar la esperanza matemática.
    /// Un valor bajo puede bloquear la estrategia prematuramente por una mala racha inicial.
    /// </summary>
    public int MinTradesForExpectancy { get; set; } = 30;

    /// <summary>Intervalo en segundos del watchdog de ticks. Default 60.</summary>
    public int WatchdogIntervalSeconds { get; set; } = 60;

    /// <summary>Intervalo en segundos del checker de drawdown. Default 30.</summary>
    public int DrawdownCheckIntervalSeconds { get; set; } = 30;

    /// <summary>
    /// Balance virtual simulado para Paper Trading en USDT. 0 = ilimitado (comportamiento anterior).
    /// Cuando es mayor que 0, las órdenes Paper se validan contra este balance menos las posiciones abiertas.
    /// </summary>
    public decimal PaperTradingBalanceUsdt { get; set; }
}
