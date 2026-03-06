using Microsoft.EntityFrameworkCore;
using TradingBot.Core.Common;
using TradingBot.Core.Entities;
using TradingBot.Core.Interfaces;

namespace TradingBot.Infrastructure.Persistence;

public sealed class TradingBotDbContext(DbContextOptions<TradingBotDbContext> options)
    : DbContext(options), IUnitOfWork
{
    public DbSet<TradingStrategy> TradingStrategies => Set<TradingStrategy>();
    public DbSet<Order>           Orders            => Set<Order>();
    public DbSet<Position>        Positions         => Set<Position>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(TradingBotDbContext).Assembly);
        base.OnModelCreating(modelBuilder);
    }

    /// <summary>
    /// Guarda los cambios y recoge los domain events de todos los agregados
    /// modificados para que puedan ser despachados por el Application layer.
    /// </summary>
    public override async Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        var result = await base.SaveChangesAsync(cancellationToken);

        // Limpiar los domain events tras persistir (el despacho se hace en el Application layer)
        var aggregatesWithEvents = ChangeTracker
            .Entries<Entity<Guid>>()
            .Where(e => e.Entity.DomainEvents.Count > 0)
            .Select(e => e.Entity)
            .ToList();

        foreach (var aggregate in aggregatesWithEvents)
            aggregate.ClearDomainEvents();

        return result;
    }
}
