using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TradingBot.Core.Enums;
using TradingBot.Core.Interfaces.Services;

namespace TradingBot.Application.AutoPilot;

/// <summary>
/// BackgroundService que monitorea el régimen de mercado de las estrategias activas
/// y delega la rotación al <see cref="IStrategyRotator"/>.
/// Ejecuta cada 5 minutos por defecto.
/// </summary>
internal sealed class AutoPilotWorker : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IStrategyEngine _engine;
    private readonly IOptionsMonitor<AutoPilotConfig> _configMonitor;
    private readonly ILogger<AutoPilotWorker> _logger;

    private static readonly TimeSpan CheckInterval = TimeSpan.FromMinutes(5);

    public AutoPilotWorker(
        IServiceScopeFactory scopeFactory,
        IStrategyEngine engine,
        IOptionsMonitor<AutoPilotConfig> configMonitor,
        ILogger<AutoPilotWorker> logger)
    {
        _scopeFactory = scopeFactory;
        _engine = engine;
        _configMonitor = configMonitor;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("AutoPilotWorker iniciado");

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
                var statuses = await _engine.GetStatusAsync(stoppingToken);

                var symbolRegimes = statuses.Values
                    .Where(s => s.CurrentRegime != MarketRegime.Unknown)
                    .GroupBy(s => s.Symbol.Value, StringComparer.OrdinalIgnoreCase)
                    .Select(g => g.First())
                    .ToList();

                if (symbolRegimes.Count > 0)
                {
                    using var scope = _scopeFactory.CreateScope();
                    var rotator = scope.ServiceProvider.GetRequiredService<IStrategyRotator>();
                    var notifier = scope.ServiceProvider.GetService<ITradingNotifier>();

                    foreach (var status in symbolRegimes)
                    {
                        var result = await rotator.EvaluateRotationAsync(
                            status.Symbol.Value, status.CurrentRegime, status.IsBullish, stoppingToken);

                        if (result.IsSuccess && result.Value.Rotated && notifier is not null)
                        {
                            await notifier.NotifyAlertAsync(
                                $"🔄 AutoPilot rotó en {status.Symbol.Value}: " +
                                $"{result.Value.DeactivatedStrategy ?? "—"} → {result.Value.ActivatedStrategy ?? "—"} " +
                                $"(régimen: {result.Value.Regime})",
                                stoppingToken);
                        }
                    }
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error en el ciclo del AutoPilot");
            }

            await Task.Delay(CheckInterval, stoppingToken);
        }

        _logger.LogInformation("AutoPilotWorker detenido");
    }
}
