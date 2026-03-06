using TradingBot.Core.Common;
using TradingBot.Core.Entities;

namespace TradingBot.Core.Interfaces.Services;

/// <summary>
/// Gestor de órdenes. Envía órdenes al exchange o las simula en Paper Trading.
/// Siempre invoca al <see cref="IRiskManager"/> antes de ejecutar cualquier orden.
/// Las implementaciones concretas están en TradingBot.Infrastructure.
/// </summary>
public interface IOrderService
{
    /// <summary>
    /// Valida la orden con el RiskManager y la envía al exchange (o la simula).
    /// Devuelve la orden actualizada con el estado resultante.
    /// </summary>
    Task<Result<Order, DomainError>> PlaceOrderAsync(
        Order order,
        CancellationToken cancellationToken = default);

    /// <summary>Cancela una orden activa tanto en el dominio como en Binance.</summary>
    Task<Result<Order, DomainError>> CancelOrderAsync(
        Guid orderId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Sincroniza el estado de una orden con Binance REST.
    /// Útil para órdenes Limit que pueden haberse llenado entre ciclos.
    /// </summary>
    Task<Result<Order, DomainError>> SyncOrderStatusAsync(
        Guid orderId,
        CancellationToken cancellationToken = default);

    /// <summary>Devuelve todas las órdenes en estado no terminal.</summary>
    Task<Result<IReadOnlyList<Order>, DomainError>> GetOpenOrdersAsync(
        CancellationToken cancellationToken = default);
}
