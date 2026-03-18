using TradingBot.Core.Entities;
using TradingBot.Core.Enums;

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

    /// <summary>
    /// Procesa una orden completamente llenada con motivo de cierre explícito.
    /// </summary>
    Task HandleOrderFilledAsync(Order order, CloseReason? closeReason, CancellationToken cancellationToken = default);

    /// <summary>
    /// Procesa un llenado parcial: crea o actualiza posición con la cantidad acumulada.
    /// </summary>
    Task HandlePartialFillAsync(Order order, CancellationToken cancellationToken = default);
}
