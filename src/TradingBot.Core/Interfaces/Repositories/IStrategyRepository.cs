using TradingBot.Core.Entities;
using TradingBot.Core.ValueObjects;

namespace TradingBot.Core.Interfaces.Repositories;

/// <summary>
/// Repositorio de estrategias de trading.
/// Incluye métodos específicos de dominio necesarios para el motor y el hot-reload.
/// </summary>
public interface IStrategyRepository : IRepository<TradingStrategy, Guid>
{
    /// <summary>Devuelve todas las estrategias con estado Active.</summary>
    Task<IReadOnlyList<TradingStrategy>> GetActiveStrategiesAsync(
        CancellationToken cancellationToken = default);

    /// <summary>Devuelve estrategias configuradas para un símbolo concreto.</summary>
    Task<IReadOnlyList<TradingStrategy>> GetBySymbolAsync(
        Symbol symbol,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Carga una estrategia incluyendo todas sus reglas e indicadores.
    /// Equivale a <see cref="IRepository{T,TId}.GetByIdAsync"/> con eager loading.
    /// </summary>
    Task<TradingStrategy?> GetWithRulesAsync(
        Guid id,
        CancellationToken cancellationToken = default);
}
