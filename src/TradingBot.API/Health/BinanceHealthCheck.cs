using Binance.Net.Interfaces.Clients;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace TradingBot.API.Health;

/// <summary>
/// Verifica la conectividad con Binance API mediante <c>GET /api/v3/ping</c>.
/// </summary>
internal sealed class BinanceHealthCheck(IBinanceRestClient restClient) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var result = await restClient.SpotApi.ExchangeData.PingAsync(cancellationToken);

            return result.Success
                ? HealthCheckResult.Healthy("Binance API respondió al ping.")
                : HealthCheckResult.Degraded($"Binance API no respondió: {result.Error?.Message}");
        }
        catch (Exception ex)
        {
            return HealthCheckResult.Unhealthy("No se pudo conectar a Binance API.", ex);
        }
    }
}
