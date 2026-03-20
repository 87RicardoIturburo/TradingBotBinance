namespace TradingBot.Core.Events;

/// <summary>
/// Publicado cuando el presupuesto de riesgo global se agota.
/// Dispara la pausa de todas las estrategias activas (kill switch).
/// </summary>
public sealed record BudgetExhaustedEvent(
    decimal TotalCapital,
    decimal MaxLossAllowed,
    decimal AccumulatedLoss) : DomainEvent;
