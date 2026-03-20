using TradingBot.Core.Common;
using TradingBot.Core.Enums;

namespace TradingBot.Core.Interfaces.Services;

/// <summary>
/// Rotador automático de estrategias según régimen de mercado.
/// Cada instancia opera sobre un symbol específico, manteniendo
/// una estrategia activa y rotando cuando cambia el régimen.
/// </summary>
public interface IStrategyRotator
{
    /// <summary>
    /// Evalúa si se requiere rotación para el symbol dado basándose
    /// en el régimen actual y la estrategia activa.
    /// </summary>
    Task<Result<RotationResult, DomainError>> EvaluateRotationAsync(
        string symbol,
        MarketRegime currentRegime,
        bool isBullish,
        CancellationToken cancellationToken = default);
}

/// <summary>Resultado de una evaluación de rotación.</summary>
public sealed record RotationResult(
    bool Rotated,
    string? DeactivatedStrategy,
    string? ActivatedStrategy,
    MarketRegime Regime,
    string Reason);
