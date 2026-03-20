namespace TradingBot.Core.Enums;

/// <summary>
/// Nivel de riesgo global basado en la pérdida acumulada vs el presupuesto máximo.
/// Controla la reducción progresiva de exposición.
/// </summary>
public enum RiskLevel
{
    /// <summary>Pérdida acumulada 0–30% del máximo. Operación normal.</summary>
    Normal,

    /// <summary>Pérdida acumulada 30–60%. Se reduce MaxOrderAmountUsdt al 70%.</summary>
    Reduced,

    /// <summary>Pérdida acumulada 60–80%. Se reduce al 40%, MaxOpenPositions = 1.</summary>
    Critical,

    /// <summary>Pérdida acumulada 80–100%. Solo cerrar posiciones, no abrir nuevas.</summary>
    CloseOnly,

    /// <summary>Pérdida acumulada ≥ 100%. Kill switch: todas las estrategias pausadas.</summary>
    Exhausted
}
