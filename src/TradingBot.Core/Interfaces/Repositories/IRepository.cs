using TradingBot.Core.Common;

namespace TradingBot.Core.Interfaces.Repositories;

/// <summary>
/// Repositorio genérico base para todos los aggregate roots.
/// Las implementaciones concretas están en TradingBot.Infrastructure.
/// </summary>
public interface IRepository<TEntity, TId>
    where TEntity : AggregateRoot<TId>
    where TId     : notnull
{
    Task<TEntity?>                   GetByIdAsync(TId id,                                CancellationToken cancellationToken = default);
    Task<IReadOnlyList<TEntity>>     GetAllAsync(                                        CancellationToken cancellationToken = default);
    Task                             AddAsync(TEntity entity,                            CancellationToken cancellationToken = default);
    Task                             UpdateAsync(TEntity entity,                         CancellationToken cancellationToken = default);
    Task                             DeleteAsync(TId id,                                 CancellationToken cancellationToken = default);
    Task<bool>                       ExistsAsync(TId id,                                 CancellationToken cancellationToken = default);
}
