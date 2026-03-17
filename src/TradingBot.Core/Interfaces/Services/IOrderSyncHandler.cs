using TradingBot.Core.Entities;

namespace TradingBot.Core.Interfaces.Services;

/// <summary>
/// Maneja la sincronización entre órdenes llenadas y posiciones.
/// Compartido entre <see cref="IOrderService"/> (ejecución directa) y
/// <c>UserDataStreamService</c> (eventos WebSocket de Binance).
/// No llama a <c>SaveChangesAsync</c> — el consumidor debe persistir los cambios.
/// </summary>
public interface IOrderSyncHandler
{
    /// <summary>
    /// Procesa una orden completamente llenada: crea una nueva posición (Buy)
    /// o cierra la posición opuesta existente (Sell).
    /// </summary>
    Task HandleOrderFilledAsync(Order order, CancellationToken cancellationToken = default);
}
