using Microsoft.EntityFrameworkCore;
using TradingBot.Core.Common;
using TradingBot.Core.Entities;
using TradingBot.Core.Interfaces;
using TradingBot.Core.ValueObjects;

namespace TradingBot.Infrastructure.Persistence;

public sealed class TradingBotDbContext(DbContextOptions<TradingBotDbContext> options)
    : DbContext(options), IUnitOfWork
{
    public DbSet<TradingStrategy> TradingStrategies => Set<TradingStrategy>();
    public DbSet<Order>           Orders            => Set<Order>();
    public DbSet<Position>        Positions         => Set<Position>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        // IndicatorConfig se serializa a JSON via value converter en TradingStrategyConfiguration.
        // Se ignora como tipo para evitar que EF Core intente mapear IReadOnlyDictionary<string, decimal>.
        modelBuilder.Ignore<IndicatorConfig>();

        modelBuilder.ApplyConfigurationsFromAssembly(typeof(TradingBotDbContext).Assembly);
        base.OnModelCreating(modelBuilder);
    }

    /// <summary>
    /// Guarda los cambios y recoge los domain events de todos los agregados
    /// modificados para que puedan ser despachados por el Application layer.
    /// </summary>
    public override async Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        FixNewOwnedEntitiesTrackedAsModified();

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

    /// <summary>
    /// Cuando DetectChanges() descubre una owned entity nueva en una colección
    /// OwnsMany cuya PK tiene ValueGeneratedOnAdd y un valor non-default
    /// (p. ej. Guid.NewGuid()), EF Core la marca como Modified en vez de Added.
    /// Esto genera un UPDATE sobre una fila inexistente → DbUpdateConcurrencyException.
    ///
    /// La firma es inequívoca: todas las propiedades tienen Original == Current
    /// porque la entidad nunca fue cargada de la BD.
    /// </summary>
    private void FixNewOwnedEntitiesTrackedAsModified()
    {
        ChangeTracker.DetectChanges();

        foreach (var entry in ChangeTracker.Entries())
        {
            if (entry.State != EntityState.Modified || !entry.Metadata.IsOwned())
                continue;

            var allOriginalEqualsCurrent = entry.Properties
                .Where(p => !p.Metadata.IsPrimaryKey())
                .All(p => Equals(p.OriginalValue, p.CurrentValue));

            if (allOriginalEqualsCurrent)
                entry.State = EntityState.Added;
        }
    }
}
