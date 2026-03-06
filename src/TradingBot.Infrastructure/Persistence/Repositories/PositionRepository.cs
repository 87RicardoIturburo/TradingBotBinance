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
        var today = DateTimeOffset.UtcNow.Date;
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
}
