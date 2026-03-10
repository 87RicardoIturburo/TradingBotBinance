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
}
