using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TradingBot.Core.Interfaces.Services;

namespace TradingBot.Application.Scanner;

/// <summary>
/// BackgroundService que ejecuta el Market Scanner periódicamente.
/// Publica los resultados via ITradingNotifier (SignalR).
/// </summary>
internal sealed class MarketScannerWorker : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IOptionsMonitor<MarketScannerConfig> _configMonitor;
    private readonly ILogger<MarketScannerWorker> _logger;

    public MarketScannerWorker(
        IServiceScopeFactory scopeFactory,
        IOptionsMonitor<MarketScannerConfig> configMonitor,
        ILogger<MarketScannerWorker> logger)
    {
        _scopeFactory = scopeFactory;
        _configMonitor = configMonitor;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("MarketScannerWorker iniciado");

        while (!stoppingToken.IsCancellationRequested)
        {
            var config = _configMonitor.CurrentValue;

            if (!config.Enabled)
            {
                await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken);
                continue;
            }

            try
            {
                using var scope = _scopeFactory.CreateScope();
                var scanner = scope.ServiceProvider.GetRequiredService<IMarketScanner>();
                var notifier = scope.ServiceProvider.GetService<ITradingNotifier>();

                var result = await scanner.ScanAsync(config.TopSymbolsCount, stoppingToken);

                if (result.IsSuccess && notifier is not null)
                {
                    await notifier.NotifyScannerUpdateAsync(result.Value, stoppingToken);
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error en el ciclo del Market Scanner");
            }

            await Task.Delay(TimeSpan.FromMinutes(config.ScanIntervalMinutes), stoppingToken);
        }

        _logger.LogInformation("MarketScannerWorker detenido");
    }
}
