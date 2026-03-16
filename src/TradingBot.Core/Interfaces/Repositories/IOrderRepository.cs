using TradingBot.Core.Entities;
using TradingBot.Core.Enums;
using TradingBot.Core.ValueObjects;

namespace TradingBot.Core.Interfaces.Repositories;

/// <summary>
/// Repositorio de órdenes. Solo lectura desde fuera del OrderManager.
/// Las escrituras ocurren a través de los métodos de dominio de <see cref="Order"/>.
/// </summary>
public interface IOrderRepository : IRepository<Order, Guid>
{
    Task<IReadOnlyList<Order>> GetByStrategyIdAsync(
        Guid strategyId,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<Order>> GetOpenOrdersAsync(
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<Order>> GetBySymbolAsync(
        Symbol symbol,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<Order>> GetByDateRangeAsync(
        DateTimeOffset from,
        DateTimeOffset to,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Devuelve órdenes con estado <see cref="OrderStatus.Submitted"/>
    /// o <see cref="OrderStatus.PartiallyFilled"/> para sincronizar con Binance.
    /// </summary>
    Task<IReadOnlyList<Order>> GetPendingSyncAsync(
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Verifica si ya existe una orden de cierre no terminal para la estrategia,
    /// símbolo y dirección indicados. Evita duplicar órdenes de cierre al reiniciar.
    /// </summary>
    Task<bool> HasPendingCloseOrderAsync(
        Guid strategyId,
        Symbol symbol,
        OrderSide exitSide,
        CancellationToken cancellationToken = default);
}
