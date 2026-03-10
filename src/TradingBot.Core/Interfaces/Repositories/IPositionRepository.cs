using TradingBot.Core.Entities;
using TradingBot.Core.ValueObjects;

namespace TradingBot.Core.Interfaces.Repositories;

/// <summary>
/// Repositorio de posiciones abiertas y cerradas.
/// Fuente de verdad para el cálculo de exposición y P&amp;L.
/// </summary>
public interface IPositionRepository : IRepository<Position, Guid>
{
    /// <summary>Devuelve todas las posiciones con <c>IsOpen = true</c>.</summary>
    Task<IReadOnlyList<Position>> GetOpenPositionsAsync(
        CancellationToken cancellationToken = default);

    /// <summary>Devuelve posiciones abiertas filtradas por estrategia.</summary>
    Task<IReadOnlyList<Position>> GetOpenByStrategyIdAsync(
        Guid strategyId,
        CancellationToken cancellationToken = default);

    /// <summary>Devuelve posiciones cerradas en un rango de fechas.</summary>
    Task<IReadOnlyList<Position>> GetClosedByDateRangeAsync(
        DateTimeOffset from,
        DateTimeOffset to,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Suma del P&amp;L realizado de todas las posiciones cerradas de una estrategia
    /// en el día UTC actual. Usado por el <c>RiskManager</c> para el límite diario.
    /// </summary>
    Task<decimal> GetDailyRealizedPnLAsync(
        Guid strategyId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Número de posiciones actualmente abiertas para una estrategia.
    /// Usado por el <c>RiskManager</c> para validar <c>MaxOpenPositions</c>.
    /// </summary>
    Task<int> GetOpenPositionCountAsync(
        Guid strategyId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Suma del P&amp;L realizado de todas las posiciones cerradas de una estrategia (histórico completo).
    /// </summary>
    Task<decimal> GetTotalRealizedPnLAsync(
        Guid strategyId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Obtiene estadísticas de trades cerrados para calcular la esperanza matemática.
    /// Devuelve (totalTrades, wins, totalWinAmount, totalLossAmount).
    /// </summary>
    Task<(int TotalTrades, int Wins, decimal TotalWinAmount, decimal TotalLossAmount)> GetTradeStatsAsync(
        Guid strategyId,
        CancellationToken cancellationToken = default);
}
