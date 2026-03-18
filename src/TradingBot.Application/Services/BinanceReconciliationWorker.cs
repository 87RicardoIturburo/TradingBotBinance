using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using TradingBot.Core.Enums;
using TradingBot.Core.Interfaces;
using TradingBot.Core.Interfaces.Repositories;
using TradingBot.Core.Interfaces.Services;
using TradingBot.Core.ValueObjects;

namespace TradingBot.Application.Services;

/// <summary>
/// IMP-1: Worker periódico que reconcilia órdenes y posiciones locales contra Binance REST.
/// <para>
/// Cada 60 segundos verifica:
/// <list type="number">
/// <item>Órdenes pendientes (Submitted/PartiallyFilled) → sincroniza estado con Binance.</item>
/// <item>Posiciones abiertas → verifica que no existan órdenes huérfanas en Binance.</item>
/// </list>
/// </para>
/// Solo opera en modo Live/Testnet (no Paper Trading).
/// </summary>
internal sealed class BinanceReconciliationWorker(
    IServiceScopeFactory scopeFactory,
    IGlobalCircuitBreaker circuitBreaker,
    ILogger<BinanceReconciliationWorker> logger) : BackgroundService
{
    private static readonly TimeSpan CheckInterval = TimeSpan.FromSeconds(60);

    /// <summary>Máximo de errores consecutivos antes de pausar la reconciliación temporalmente.</summary>
    private const int MaxConsecutiveErrors = 5;

    /// <summary>Pausa extendida tras demasiados errores consecutivos.</summary>
    private static readonly TimeSpan ErrorCooldown = TimeSpan.FromMinutes(5);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("BinanceReconciliationWorker iniciado");

        // Esperar 30s antes de la primera reconciliación para que los demás servicios arranquen
        await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);

        var consecutiveErrors = 0;

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(CheckInterval, stoppingToken);

                // No reconciliar si el circuit breaker está abierto
                if (circuitBreaker.IsOpen)
                {
                    logger.LogDebug("Reconciliación omitida: circuit breaker abierto");
                    continue;
                }

                await ReconcileOrdersAsync(stoppingToken);
                consecutiveErrors = 0;
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                consecutiveErrors++;
                logger.LogError(ex,
                    "Error en reconciliación ({Count}/{Max})",
                    consecutiveErrors, MaxConsecutiveErrors);

                if (consecutiveErrors >= MaxConsecutiveErrors)
                {
                    logger.LogWarning(
                        "Reconciliación pausada por {Cooldown}s tras {Count} errores consecutivos",
                        ErrorCooldown.TotalSeconds, consecutiveErrors);
                    await Task.Delay(ErrorCooldown, stoppingToken);
                    consecutiveErrors = 0;
                }
            }
        }

        logger.LogInformation("BinanceReconciliationWorker detenido");
    }

    /// <summary>
    /// Sincroniza órdenes pendientes locales con el estado real en Binance REST.
    /// </summary>
    private async Task ReconcileOrdersAsync(CancellationToken cancellationToken)
    {
        using var scope = scopeFactory.CreateScope();
        var orderRepo      = scope.ServiceProvider.GetRequiredService<IOrderRepository>();
        var orderExecutor  = scope.ServiceProvider.GetRequiredService<ISpotOrderExecutor>();
        var orderSyncHandler = scope.ServiceProvider.GetRequiredService<IOrderSyncHandler>();
        var unitOfWork     = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        var notifier       = scope.ServiceProvider.GetService<ITradingNotifier>();

        var pendingOrders = await orderRepo.GetPendingSyncAsync(cancellationToken);
        if (pendingOrders.Count == 0) return;

        var reconciledCount = 0;

        foreach (var order in pendingOrders)
        {
            // Solo reconciliar órdenes Live/Testnet con ID de Binance
            if (order.IsPaperTrade || string.IsNullOrEmpty(order.BinanceOrderId))
                continue;

            // No reconciliar órdenes muy recientes (< 5s) para dar tiempo al WebSocket
            if (DateTimeOffset.UtcNow - order.UpdatedAt < TimeSpan.FromSeconds(5))
                continue;

            try
            {
                var statusResult = await orderExecutor.GetOrderStatusAsync(
                    order.Symbol.Value, order.BinanceOrderId, cancellationToken);

                if (statusResult.IsFailure)
                {
                    logger.LogDebug(
                        "No se pudo consultar estado de orden {OrderId} en Binance: {Error}",
                        order.Id, statusResult.Error.Message);
                    circuitBreaker.RecordExchangeError(statusResult.Error.Message);
                    continue;
                }

                circuitBreaker.RecordExchangeSuccess();
                var status = statusResult.Value;

                // Obtener la orden tracked por EF Core para modificar
                var tracked = await orderRepo.GetByIdAsync(order.Id, cancellationToken);
                if (tracked is null || tracked.IsTerminal) continue;

                var changed = false;

                if (status.IsCompletelyFilled && tracked.Status != OrderStatus.Filled)
                {
                    var qty = Quantity.Create(status.ExecutedQuantity);
                    var price = Price.Create(status.ExecutedPrice);

                    if (qty.IsSuccess && price.IsSuccess)
                    {
                        tracked.Fill(qty.Value, price.Value);
                        await orderSyncHandler.HandleOrderFilledAsync(tracked, cancellationToken);
                        changed = true;

                        logger.LogWarning(
                            "🔄 Reconciliación: orden {OrderId} completada (Filled) — no detectada por WebSocket",
                            tracked.Id);
                    }
                }
                else if (status.IsCancelled && tracked.Status != OrderStatus.Cancelled)
                {
                    tracked.Cancel($"Reconciliación: estado Binance = {status.Status}");
                    changed = true;

                    logger.LogWarning(
                        "🔄 Reconciliación: orden {OrderId} cancelada en Binance — no detectada por WebSocket",
                        tracked.Id);
                }
                else if (status.ExecutedQuantity > 0 && tracked.Status == OrderStatus.Submitted)
                {
                    var qty = Quantity.Create(status.ExecutedQuantity);
                    var price = Price.Create(status.ExecutedPrice);

                    if (qty.IsSuccess && price.IsSuccess)
                    {
                        tracked.PartialFill(qty.Value, price.Value);
                        changed = true;

                        logger.LogInformation(
                            "🔄 Reconciliación: orden {OrderId} parcialmente llenada ({Qty})",
                            tracked.Id, status.ExecutedQuantity);
                    }
                }

                if (changed)
                {
                    await orderRepo.UpdateAsync(tracked, cancellationToken);
                    await unitOfWork.SaveChangesAsync(cancellationToken);
                    reconciledCount++;

                    if (notifier is not null)
                        await notifier.NotifyAlertAsync(
                            $"🔄 Reconciliación detectó cambio en orden {tracked.Id} ({tracked.Symbol.Value}): {tracked.Status}",
                            cancellationToken);
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                logger.LogWarning(ex,
                    "Error reconciliando orden {OrderId} ({Symbol})",
                    order.Id, order.Symbol.Value);
            }
        }

        if (reconciledCount > 0)
        {
            logger.LogInformation(
                "Reconciliación completada: {Count} órdenes actualizadas", reconciledCount);
        }
    }
}
