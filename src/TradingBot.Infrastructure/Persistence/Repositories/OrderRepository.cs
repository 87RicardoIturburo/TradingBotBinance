using Microsoft.EntityFrameworkCore;
using TradingBot.Core.Entities;
using TradingBot.Core.Enums;
using TradingBot.Core.Interfaces.Repositories;
using TradingBot.Core.ValueObjects;

namespace TradingBot.Infrastructure.Persistence.Repositories;

internal sealed class OrderRepository(TradingBotDbContext context)
    : RepositoryBase<Order, Guid>(context), IOrderRepository
{
    /// <summary>Estados que no han alcanzado un estado terminal.</summary>
    private static readonly OrderStatus[] OpenStatuses =
    [
        OrderStatus.Pending,
        OrderStatus.Submitted,
        OrderStatus.PartiallyFilled
    ];

    /// <summary>Estados que requieren sincronización con Binance REST.</summary>
    private static readonly OrderStatus[] SyncStatuses =
    [
        OrderStatus.Submitted,
        OrderStatus.PartiallyFilled
    ];

    public async Task<IReadOnlyList<Order>> GetByStrategyIdAsync(
        Guid strategyId,
        CancellationToken cancellationToken = default)
        => await DbSet
            .AsNoTracking()
            .Where(o => o.StrategyId == strategyId)
            .OrderByDescending(o => o.CreatedAt)
            .ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<Order>> GetOpenOrdersAsync(
        CancellationToken cancellationToken = default)
        => await DbSet
            .AsNoTracking()
            .Where(o => OpenStatuses.Contains(o.Status))
            .OrderByDescending(o => o.CreatedAt)
            .ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<Order>> GetBySymbolAsync(
        Symbol symbol,
        CancellationToken cancellationToken = default)
        => await DbSet
            .AsNoTracking()
            .Where(o => o.Symbol == symbol)
            .OrderByDescending(o => o.CreatedAt)
            .ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<Order>> GetByDateRangeAsync(
        DateTimeOffset from,
        DateTimeOffset to,
        CancellationToken cancellationToken = default)
        => await DbSet
            .AsNoTracking()
            .Where(o => o.CreatedAt >= from && o.CreatedAt <= to)
            .OrderByDescending(o => o.CreatedAt)
            .ToListAsync(cancellationToken);

    /// <summary>
    /// Órdenes en estado Submitted o PartiallyFilled que deben sincronizarse
    /// con Binance para detectar llenados fuera de banda.
    /// </summary>
    public async Task<IReadOnlyList<Order>> GetPendingSyncAsync(
        CancellationToken cancellationToken = default)
        => await DbSet
            .AsNoTracking()
            .Where(o => SyncStatuses.Contains(o.Status))
            .ToListAsync(cancellationToken);
}
