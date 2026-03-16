using MediatR;
using Microsoft.EntityFrameworkCore;
using TradingBot.Application.EventHandlers;
using TradingBot.Core.Common;
using TradingBot.Core.Entities;
using TradingBot.Core.Interfaces;
using TradingBot.Core.ValueObjects;

namespace TradingBot.Infrastructure.Persistence;

public sealed class TradingBotDbContext : DbContext, IUnitOfWork
{
    private readonly IMediator _mediator;

    public TradingBotDbContext(
        DbContextOptions<TradingBotDbContext> options,
        IMediator mediator)
        : base(options)
    {
        _mediator = mediator;
    }

    public DbSet<TradingStrategy> TradingStrategies => Set<TradingStrategy>();
    public DbSet<Order>           Orders            => Set<Order>();
    public DbSet<Position>        Positions         => Set<Position>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Ignore<IndicatorConfig>();
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(TradingBotDbContext).Assembly);
        base.OnModelCreating(modelBuilder);
    }

    /// <summary>
    /// Guarda los cambios, luego despacha los domain events vía MediatR.
    /// Patrón: persist primero → dispatch después (garantiza consistencia).
    /// </summary>
    public override async Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        FixNewOwnedEntitiesTrackedAsModified();

        // Recoger eventos ANTES de persistir (las entidades pueden dejar de ser tracked)
        var domainEvents = ChangeTracker
            .Entries<Entity<Guid>>()
            .Where(e => e.Entity.DomainEvents.Count > 0)
            .SelectMany(e => e.Entity.DomainEvents)
            .ToList();

        // Limpiar eventos de las entidades para evitar despacho doble
        foreach (var entry in ChangeTracker.Entries<Entity<Guid>>())
            entry.Entity.ClearDomainEvents();

        var result = await base.SaveChangesAsync(cancellationToken);

        // Despachar eventos después de persistir exitosamente
        foreach (var domainEvent in domainEvents)
        {
            // Envolver IDomainEvent como MediatR INotification
            await _mediator.Publish(
                new DomainEventNotification(domainEvent), cancellationToken);
        }

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
