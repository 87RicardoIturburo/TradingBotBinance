using Microsoft.AspNetCore.SignalR;
using TradingBot.API.Hubs;
using TradingBot.Application.Diagnostics;
using TradingBot.Core.Interfaces.Services;

namespace TradingBot.API.Services;

/// <summary>
/// Servicio en background que publica un snapshot de métricas cada 5 segundos
/// a todos los clientes SignalR conectados. Alimenta el dashboard de métricas en tiempo real.
/// </summary>
internal sealed class MetricsBroadcastService(
    TradingMetrics metrics,
    IGlobalCircuitBreaker circuitBreaker,
    IHubContext<TradingHub> hubContext,
    ILogger<MetricsBroadcastService> logger) : BackgroundService
{
    private static readonly TimeSpan BroadcastInterval = TimeSpan.FromSeconds(5);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("MetricsBroadcastService iniciado — intervalo: {Interval}s", BroadcastInterval.TotalSeconds);

        using var timer = new PeriodicTimer(BroadcastInterval);

        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                try
                {
                    var snapshot = metrics.GetSnapshot();

                    var dto = new
                    {
                        snapshot.TotalTicksProcessed,
                        snapshot.TotalSignalsGenerated,
                        snapshot.TotalOrdersPlaced,
                        snapshot.TotalOrdersFailed,
                        snapshot.TotalTicksDropped,
                        snapshot.TotalOrdersPaper,
                        snapshot.TotalOrdersLive,
                        snapshot.LastLatencyMs,
                        snapshot.AverageLatencyMs,
                        snapshot.DailyPnLUsdt,
                        snapshot.Timestamp,
                        CircuitBreakerOpen = circuitBreaker.IsOpen,
                        CircuitBreakerReason = circuitBreaker.TripReason
                    };

                    await hubContext.Clients.All.SendAsync(
                        TradingHub.Events.OnMetricsUpdate, dto, stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Error enviando métricas por SignalR");
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Shutdown normal
        }

        logger.LogInformation("MetricsBroadcastService detenido");
    }
}
