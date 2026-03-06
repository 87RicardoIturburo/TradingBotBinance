using Microsoft.EntityFrameworkCore;
using TradingBot.Core.Entities;
using TradingBot.Core.Enums;
using TradingBot.Core.Interfaces.Repositories;
using TradingBot.Core.ValueObjects;

namespace TradingBot.Infrastructure.Persistence.Repositories;

internal sealed class StrategyRepository(TradingBotDbContext context)
    : RepositoryBase<TradingStrategy, Guid>(context), IStrategyRepository
{
    /// <summary>
    /// Carga la estrategia SIN reglas para consultas rápidas (listing, status checks).
    /// Usa <see cref="GetWithRulesAsync"/> cuando se necesita la configuración completa.
    /// </summary>
    public override async Task<TradingStrategy?> GetByIdAsync(
        Guid id,
        CancellationToken cancellationToken = default)
        => await DbSet
            .FirstOrDefaultAsync(s => s.Id == id, cancellationToken);

    public async Task<IReadOnlyList<TradingStrategy>> GetActiveStrategiesAsync(
        CancellationToken cancellationToken = default)
        => await DbSet
            .Include(s => s.Rules)
            .Where(s => s.Status == StrategyStatus.Active)
            .AsNoTracking()
            .ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<TradingStrategy>> GetBySymbolAsync(
        Symbol symbol,
        CancellationToken cancellationToken = default)
    {
        var symbolValue = symbol.Value;
        return await DbSet
            .Include(s => s.Rules)
            .Where(s => s.Symbol == symbol)
            .AsNoTracking()
            .ToListAsync(cancellationToken);
    }

    /// <summary>
    /// Carga la estrategia incluyendo todas sus reglas e indicadores (eager loading).
    /// Usado por el StrategyEngine y el ConfigService para hot-reload.
    /// </summary>
    public async Task<TradingStrategy?> GetWithRulesAsync(
        Guid id,
        CancellationToken cancellationToken = default)
        => await DbSet
            .Include(s => s.Rules)
            .FirstOrDefaultAsync(s => s.Id == id, cancellationToken);
}
