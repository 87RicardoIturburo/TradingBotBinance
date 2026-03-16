using Microsoft.Extensions.Diagnostics.HealthChecks;
using TradingBot.Core.Interfaces.Services;

namespace TradingBot.API.Health;

/// <summary>
/// Verifica que el motor de estrategias esté en ejecución y que los runners activos
/// hayan recibido ticks recientemente (últimos 5 minutos).
/// </summary>
internal sealed class StrategyEngineHealthCheck(IStrategyEngine strategyEngine) : IHealthCheck
{
    private static readonly TimeSpan StaleThreshold = TimeSpan.FromMinutes(5);

    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        if (!strategyEngine.IsRunning)
            return HealthCheckResult.Degraded("Motor de estrategias detenido o sin runners activos.");

        var statuses = await strategyEngine.GetStatusAsync(cancellationToken);

        if (statuses.Count == 0)
            return HealthCheckResult.Healthy("Motor en ejecución, sin estrategias activas.");

        var now = DateTimeOffset.UtcNow;
        var staleRunners = statuses.Values
            .Where(s => s.IsProcessing && s.LastTickAt != default && now - s.LastTickAt > StaleThreshold)
            .ToList();

        if (staleRunners.Count > 0)
        {
            var names = string.Join(", ", staleRunners.Select(s => $"{s.StrategyName} ({s.Symbol})"));
            return HealthCheckResult.Degraded(
                $"{staleRunners.Count} runner(s) sin ticks hace >{StaleThreshold.TotalMinutes}m: {names}");
        }

        var data = new Dictionary<string, object>
        {
            ["ActiveRunners"] = statuses.Count,
            ["TotalTicks"] = statuses.Values.Sum(s => s.TicksProcessed),
            ["TotalSignals"] = statuses.Values.Sum(s => s.SignalsGenerated),
            ["TotalOrders"] = statuses.Values.Sum(s => s.OrdersPlaced)
        };

        return HealthCheckResult.Healthy(
            $"{statuses.Count} runner(s) activos, todos recibiendo ticks.", data);
    }
}
