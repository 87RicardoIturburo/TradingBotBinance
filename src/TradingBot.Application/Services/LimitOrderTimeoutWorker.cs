using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using TradingBot.Core.Enums;
using TradingBot.Core.Interfaces.Repositories;
using TradingBot.Core.Interfaces.Services;

namespace TradingBot.Application.Services;

/// <summary>
/// Background worker que cancela automáticamente las órdenes Limit que no se llenaron
/// dentro del timeout configurado en <c>RiskConfig.LimitOrderTimeoutSeconds</c>.
/// Consulta cada 5 segundos las órdenes pendientes y cancela las vencidas.
/// </summary>
internal sealed class LimitOrderTimeoutWorker(
    IServiceScopeFactory scopeFactory,
    ILogger<LimitOrderTimeoutWorker> logger) : BackgroundService
{
    private static readonly TimeSpan CheckInterval = TimeSpan.FromSeconds(5);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("LimitOrderTimeoutWorker iniciado");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(CheckInterval, stoppingToken);
                await CancelTimedOutOrdersAsync(stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error en LimitOrderTimeoutWorker");
            }
        }
    }

    private async Task CancelTimedOutOrdersAsync(CancellationToken cancellationToken)
    {
        using var scope = scopeFactory.CreateScope();
        var orderRepo    = scope.ServiceProvider.GetRequiredService<IOrderRepository>();
        var strategyRepo = scope.ServiceProvider.GetRequiredService<IStrategyRepository>();
        var orderService = scope.ServiceProvider.GetRequiredService<IOrderService>();

        var pendingOrders = await orderRepo.GetPendingSyncAsync(cancellationToken);

        var now = DateTimeOffset.UtcNow;

        foreach (var order in pendingOrders)
        {
            // Solo órdenes Limit (no Market)
            if (order.Type != OrderType.Limit) continue;

            // Solo estados no terminales
            if (order.IsTerminal) continue;

            // Obtener timeout de la estrategia
            var strategy = await strategyRepo.GetByIdAsync(order.StrategyId, cancellationToken);
            if (strategy is null) continue;

            var timeoutSeconds = strategy.RiskConfig.LimitOrderTimeoutSeconds;
            if (timeoutSeconds <= 0) continue;

            var elapsed = now - order.CreatedAt;
            if (elapsed.TotalSeconds < timeoutSeconds) continue;

            logger.LogWarning(
                "Limit order {OrderId} expirada: {Elapsed:F0}s > timeout {Timeout}s. Cancelando.",
                order.Id, elapsed.TotalSeconds, timeoutSeconds);

            var cancelResult = await orderService.CancelOrderAsync(order.Id, cancellationToken);
            if (cancelResult.IsSuccess)
            {
                logger.LogInformation(
                    "Limit order {OrderId} cancelada por timeout ({Symbol} {Side})",
                    order.Id, order.Symbol.Value, order.Side);
            }
            else
            {
                logger.LogWarning(
                    "No se pudo cancelar Limit order {OrderId}: {Error}",
                    order.Id, cancelResult.Error.Message);
            }
        }
    }
}
