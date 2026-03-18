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
        await HandleOrderFilledAsync(order, closeReason: null, cancellationToken);
    }

    public async Task HandleOrderFilledAsync(Order order, CloseReason? closeReason, CancellationToken cancellationToken = default)
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
            var reason = closeReason ?? CloseReason.Manual;
            positionToClose.Close(order.ExecutedPrice, order.Fee, reason);
            await positionRepository.UpdateAsync(positionToClose, cancellationToken);

            logger.LogInformation(
                "Posición {PosId} cerrada ({Reason}): {Side} {Symbol} PnL={PnL:F2} (fees: entry={EntryFee:F4}, exit={ExitFee:F4})",
                positionToClose.Id, reason, positionToClose.Side, order.Symbol.Value,
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

    /// <summary>
    /// IMP-8: Procesa un llenado parcial. Para BUY: crea posición si no existe
    /// o acumula la cantidad llenada. Para SELL: loguea progreso (el cierre real
    /// se ejecuta cuando llega el evento <c>Filled</c>).
    /// </summary>
    public async Task HandlePartialFillAsync(Order order, CancellationToken cancellationToken = default)
    {
        if (order.FilledQuantity is null || order.ExecutedPrice is null)
            return;

        if (order.Side == OrderSide.Buy)
        {
            var openPositions = await positionRepository
                .GetOpenByStrategyIdAsync(order.StrategyId, cancellationToken);

            var existing = openPositions
                .FirstOrDefault(p => p.Symbol == order.Symbol && p.Side == OrderSide.Buy);

            if (existing is not null)
            {
                // Acumular: actualizar cantidad y precio promedio
                existing.AccumulatePartialFill(order.FilledQuantity, order.ExecutedPrice, 0m);
                await positionRepository.UpdateAsync(existing, cancellationToken);

                logger.LogInformation(
                    "Partial fill acumulado en posición {PosId}: {Qty} {Symbol} @ {Price}",
                    existing.Id, order.FilledQuantity.Value, order.Symbol.Value, order.ExecutedPrice.Value);
            }
            else
            {
                // Primer partial fill → crear posición con la cantidad parcial
                var position = Position.Open(
                    order.StrategyId, order.Symbol, order.Side,
                    order.ExecutedPrice, order.FilledQuantity, 0m);

                await positionRepository.AddAsync(position, cancellationToken);

                logger.LogInformation(
                    "Posición abierta por partial fill: {Side} {Qty} {Symbol} @ {Price}",
                    order.Side, order.FilledQuantity.Value, order.Symbol.Value, order.ExecutedPrice.Value);
            }
        }
        else
        {
            // Sell partial fill: la posición sigue abierta hasta el fill completo.
            // El trailing stop y SL/TP del tick loop protegen la posición mientras tanto.
            logger.LogInformation(
                "Partial fill Sell {OrderId}: {Qty} {Symbol} @ {Price} — posición se cerrará en fill completo",
                order.Id, order.FilledQuantity.Value, order.Symbol.Value, order.ExecutedPrice.Value);
        }
    }
}
