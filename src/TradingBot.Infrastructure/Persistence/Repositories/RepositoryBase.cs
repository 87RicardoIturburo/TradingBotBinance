using Microsoft.EntityFrameworkCore;
using TradingBot.Core.Common;
using TradingBot.Core.Interfaces.Repositories;

namespace TradingBot.Infrastructure.Persistence.Repositories;

/// <summary>
/// Implementación base del repositorio genérico.
/// Los métodos de escritura NO llaman a SaveChangesAsync — eso es responsabilidad
/// del handler MediatR a través de <c>IUnitOfWork</c>.
/// </summary>
internal abstract class RepositoryBase<TEntity, TId>(TradingBotDbContext context)
    : IRepository<TEntity, TId>
    where TEntity : AggregateRoot<TId>
    where TId     : notnull
{
    protected readonly TradingBotDbContext Context = context;
    protected readonly DbSet<TEntity>      DbSet   = context.Set<TEntity>();

    public virtual async Task<TEntity?> GetByIdAsync(TId id, CancellationToken cancellationToken = default)
        => await DbSet.FindAsync(new object[] { id }, cancellationToken);

    public virtual async Task<IReadOnlyList<TEntity>> GetAllAsync(CancellationToken cancellationToken = default)
        => await DbSet.AsNoTracking().ToListAsync(cancellationToken);

    public virtual async Task AddAsync(TEntity entity, CancellationToken cancellationToken = default)
        => await DbSet.AddAsync(entity, cancellationToken);

    public virtual Task UpdateAsync(TEntity entity, CancellationToken cancellationToken = default)
    {
        DbSet.Update(entity);
        return Task.CompletedTask;
    }

    public virtual async Task DeleteAsync(TId id, CancellationToken cancellationToken = default)
    {
        var entity = await GetByIdAsync(id, cancellationToken);
        if (entity is not null)
            DbSet.Remove(entity);
    }

    public virtual async Task<bool> ExistsAsync(TId id, CancellationToken cancellationToken = default)
        => await DbSet.FindAsync(new object[] { id }, cancellationToken) is not null;
}
