using TradingBot.Core.Common;
using TradingBot.Core.Entities;

namespace TradingBot.Core.Interfaces.Services;

/// <summary>
/// Gestor de riesgo. Valida TODA orden antes de que el OrderService la ejecute.
/// Es una capa de seguridad obligatoria que no puede ser evitada.
/// </summary>
public interface IRiskManager
{
    /// <summary>
    /// Valida la orden contra la <see cref="RiskConfig"/> de su estrategia.
    /// Comprueba: monto máximo por orden, pérdida diaria acumulada,
    /// número máximo de posiciones abiertas y saldo disponible.
    /// </summary>
    /// <returns>
    /// <c>Success(true)</c> si la orden pasa todas las validaciones.
    /// <c>Failure(DomainError)</c> con el límite específico que se incumple.
    /// </returns>
    Task<Result<bool, DomainError>> ValidateOrderAsync(
        Order order,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Suma de pérdidas realizadas del día UTC actual para una estrategia.
    /// Usado internamente para evaluar <c>MaxDailyLossUsdt</c>.
    /// </summary>
    Task<decimal> GetDailyLossAsync(
        Guid strategyId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Número de posiciones actualmente abiertas para una estrategia.
    /// Usado para evaluar <c>MaxOpenPositions</c>.
    /// </summary>
    Task<int> GetOpenPositionCountAsync(
        Guid strategyId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Calcula la esperanza matemática de una estrategia:
    /// <c>E = (WinRate × AvgWin) − (LossRate × AvgLoss)</c>.
    /// Devuelve null si no hay suficientes trades cerrados.
    /// </summary>
    Task<decimal?> GetMathematicalExpectancyAsync(
        Guid strategyId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Verifica si el drawdown diario de la cuenta supera el límite configurado.
    /// Devuelve si se debe activar el kill switch y el porcentaje de drawdown.
    /// </summary>
    Task<(bool IsTriggered, decimal DrawdownPercent)> CheckAccountDrawdownAsync(
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Devuelve la exposición total del portafolio (Long, Short, Neto) en USDT.
    /// </summary>
    Task<(decimal TotalLongUsdt, decimal TotalShortUsdt, decimal NetUsdt)> GetPortfolioExposureAsync(
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Devuelve la exposición del portafolio desglosada por símbolo.
    /// Cada tupla contiene: (Symbol, LongUsdt, ShortUsdt, NetUsdt).
    /// </summary>
    Task<IReadOnlyList<(string Symbol, decimal LongUsdt, decimal ShortUsdt, decimal NetUsdt)>> GetExposureBySymbolAsync(
        CancellationToken cancellationToken = default);
}
