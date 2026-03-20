using TradingBot.Core.Enums;

namespace TradingBot.Core.Interfaces.Services;

/// <summary>
/// Guardián de capital global. Calcula el nivel de riesgo actual basándose
/// en la pérdida acumulada vs el presupuesto máximo definido por el usuario.
/// Consultado por el <see cref="IRiskManager"/> antes de cada orden.
/// </summary>
public interface IRiskBudget
{
    /// <summary>Nivel de riesgo actual.</summary>
    RiskLevel CurrentLevel { get; }

    /// <summary>Pérdida acumulada total en USDT (valor absoluto positivo).</summary>
    decimal AccumulatedLoss { get; }

    /// <summary>Pérdida máxima permitida en USDT.</summary>
    decimal MaxLossAllowed { get; }

    /// <summary>Porcentaje del presupuesto consumido (0–100+).</summary>
    decimal BudgetUsedPercent { get; }

    /// <summary>
    /// Multiplicador para ajustar <c>MaxOrderAmountUsdt</c> según el nivel actual.
    /// 1.0 = sin reducción, 0.7 = Reduced, 0.4 = Critical, 0 = CloseOnly/Exhausted.
    /// </summary>
    decimal OrderAmountMultiplier { get; }

    /// <summary>
    /// Máximo de posiciones abiertas permitidas según el nivel.
    /// <c>null</c> = sin override (usar el de la estrategia).
    /// </summary>
    int? MaxOpenPositionsOverride { get; }

    /// <summary>
    /// Recalcula el nivel de riesgo leyendo el P&amp;L acumulado desde la base de datos.
    /// Llamar periódicamente o después de cerrar posiciones.
    /// </summary>
    Task RefreshAsync(CancellationToken cancellationToken = default);
}
