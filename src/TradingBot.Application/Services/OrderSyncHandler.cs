using Microsoft.Extensions.Logging;
using TradingBot.Core.Entities;
using TradingBot.Core.Enums;
using TradingBot.Core.Interfaces.Repositories;
using TradingBot.Core.Interfaces.Services;

namespace TradingBot.Application.Services;

/// <summary>
/// Implementación compartida de la sincronización orden → posición.
/// Usado por <see cref="OrderService"/> al ejecutar órdenes y por
/// <c>UserDataStreamService</c> al recibir fills vía WebSocket.
/// </summary>
internal sealed class OrderSyncHandler(
    IPositionRepository positionRepository,
    ILogger<OrderSyncHandler> logger) : IOrderSyncHandler
{
    public async Task HandleOrderFilledAsync(Order order, CancellationToken cancellationToken = default)
    {
        // Buscar si hay posición abierta del lado opuesto para cerrarla
        var oppositeSide = order.Side == OrderSide.Buy ? OrderSide.Sell : OrderSide.Buy;
        var openPositions = await positionRepository
            .GetOpenByStrategyIdAsync(order.StrategyId, cancellationToken);

        var positionToClose = openPositions
            .FirstOrDefault(p => p.Symbol == order.Symbol && p.Side == oppositeSide);

        if (positionToClose is not null && order.ExecutedPrice is not null)
        {
            // Cerrar posición existente del lado opuesto, descontando fee de salida
            positionToClose.Close(order.ExecutedPrice, order.Fee);
            await positionRepository.UpdateAsync(positionToClose, cancellationToken);

            logger.LogInformation(
                "Posición {PosId} cerrada: {Side} {Symbol} PnL={PnL:F2} (fees: entry={EntryFee:F4}, exit={ExitFee:F4})",
                positionToClose.Id, positionToClose.Side, order.Symbol.Value,
                positionToClose.RealizedPnL, positionToClose.EntryFee, positionToClose.ExitFee);
        }
        else if (order.Side == OrderSide.Buy)
        {
            // Abrir nueva posición Long (Spot solo permite Long), con fee de entrada
            var position = Position.Open(
                order.StrategyId, order.Symbol, order.Side,
                order.ExecutedPrice!, order.FilledQuantity!,
                order.Fee);

            await positionRepository.AddAsync(position, cancellationToken);

            logger.LogInformation(
                "Posición abierta: {Side} {Qty} {Symbol} @ {Price} (fee: {Fee:F4})",
                order.Side, order.FilledQuantity!.Value, order.Symbol.Value,
                order.ExecutedPrice!.Value, order.Fee);
        }
        else
        {
            // Sell sin posición Long que cerrar — no se puede abrir Short en Spot
            logger.LogWarning(
                "Orden Sell {OrderId} completada pero no hay posición Long abierta para cerrar en {Symbol}. " +
                "No se crea posición Short (Spot trading no soporta shorts).",
                order.Id, order.Symbol.Value);
        }
    }
}
