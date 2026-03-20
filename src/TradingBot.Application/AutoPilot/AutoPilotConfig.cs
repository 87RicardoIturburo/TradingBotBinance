namespace TradingBot.Application.AutoPilot;

/// <summary>
/// Configuración del Auto-Pilot / Strategy Rotator. Hot-reloadable vía appsettings.json.
/// Cada instancia opera sobre un symbol específico, rotando entre estrategias
/// según el régimen de mercado detectado.
/// </summary>
public sealed class AutoPilotConfig
{
    public const string SectionName = "AutoPilot";

    public bool Enabled { get; set; }
    public int RotationCooldownMinutes { get; set; } = 120;
    public bool ClosePositionsOnRotation { get; set; } = true;
    public string HighVolatilityAction { get; set; } = "PauseAll";

    public string TrendingTemplateId { get; set; } = "trend-rider-alcista";
    public string RangingTemplateId { get; set; } = "range-scalper-lateral";
    public string BearishTemplateId { get; set; } = "defensive-bottom-catcher-bajista";

    /// <summary>
    /// Modo de trading para las estrategias creadas por AutoPilot.
    /// Valores: PaperTrading, Testnet, Live. Default: PaperTrading.
    /// </summary>
    public string DefaultTradingMode { get; set; } = "PaperTrading";
}
