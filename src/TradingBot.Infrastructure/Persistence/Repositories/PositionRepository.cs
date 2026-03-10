using Microsoft.EntityFrameworkCore;
using TradingBot.Core.Entities;
using TradingBot.Core.Interfaces.Repositories;

namespace TradingBot.Infrastructure.Persistence.Repositories;

internal sealed class PositionRepository(TradingBotDbContext context)
    : RepositoryBase<Position, Guid>(context), IPositionRepository
{
    public async Task<IReadOnlyList<Position>> GetOpenPositionsAsync(
        CancellationToken cancellationToken = default)
        => await DbSet
            .AsNoTracking()
            .Where(p => p.IsOpen)
            .ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<Position>> GetOpenByStrategyIdAsync(
        Guid strategyId,
        CancellationToken cancellationToken = default)
        => await DbSet
            .AsNoTracking()
            .Where(p => p.IsOpen && p.StrategyId == strategyId)
            .ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<Position>> GetClosedByDateRangeAsync(
        DateTimeOffset from,
        DateTimeOffset to,
        CancellationToken cancellationToken = default)
        => await DbSet
            .AsNoTracking()
            .Where(p => !p.IsOpen && p.ClosedAt >= from && p.ClosedAt <= to)
            .OrderByDescending(p => p.ClosedAt)
            .ToListAsync(cancellationToken);

    /// <summary>
    /// Suma del P&amp;L realizado en el día UTC actual para la estrategia indicada.
    /// El RiskManager llama este método para evaluar <c>MaxDailyLossUsdt</c>.
    /// </summary>
    public async Task<decimal> GetDailyRealizedPnLAsync(
        Guid strategyId,
        CancellationToken cancellationToken = default)
    {
        var today = new DateTimeOffset(DateTimeOffset.UtcNow.Date, TimeSpan.Zero);
        return await DbSet
            .Where(p => !p.IsOpen
                     && p.StrategyId == strategyId
                     && p.ClosedAt   >= today)
            .SumAsync(p => p.RealizedPnL ?? 0m, cancellationToken);
    }

    /// <summary>
    /// Número de posiciones abiertas para la estrategia indicada.
    /// El RiskManager llama este método para evaluar <c>MaxOpenPositions</c>.
    /// </summary>
    public async Task<int> GetOpenPositionCountAsync(
        Guid strategyId,
        CancellationToken cancellationToken = default)
        => await DbSet
            .CountAsync(p => p.IsOpen && p.StrategyId == strategyId, cancellationToken);

    public async Task<decimal> GetTotalRealizedPnLAsync(
        Guid strategyId,
        CancellationToken cancellationToken = default)
        => await DbSet
            .Where(p => !p.IsOpen && p.StrategyId == strategyId)
            .SumAsync(p => p.RealizedPnL ?? 0m, cancellationToken);

    /// <summary>
    /// Obtiene estadísticas de trades cerrados para calcular la esperanza matemática.
    /// Ejecuta una sola query con GroupBy para eficiencia.
    /// </summary>
    public async Task<(int TotalTrades, int Wins, decimal TotalWinAmount, decimal TotalLossAmount)> GetTradeStatsAsync(
        Guid strategyId,
        CancellationToken cancellationToken = default)
    {
        var stats = await DbSet
            .Where(p => !p.IsOpen && p.StrategyId == strategyId && p.RealizedPnL != null)
            .GroupBy(_ => 1)
            .Select(g => new
            {
                TotalTrades    = g.Count(),
                Wins           = g.Count(p => p.RealizedPnL > 0m),
                TotalWinAmount = g.Where(p => p.RealizedPnL > 0m).Sum(p => p.RealizedPnL ?? 0m),
                TotalLossAmount = g.Where(p => p.RealizedPnL <= 0m).Sum(p => -(p.RealizedPnL ?? 0m))
            })
            .FirstOrDefaultAsync(cancellationToken);

        return stats is null
            ? (0, 0, 0m, 0m)
            : (stats.TotalTrades, stats.Wins, stats.TotalWinAmount, stats.TotalLossAmount);
    }
}
