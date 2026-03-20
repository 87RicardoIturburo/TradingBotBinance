using Microsoft.AspNetCore.SignalR;

namespace TradingBot.API.Hubs;

/// <summary>
/// Hub de SignalR para comunicación en tiempo real con el frontend Blazor WASM.
/// Los eventos se publican desde el <c>StrategyEngine</c> vía <c>IHubContext&lt;TradingHub&gt;</c>.
/// </summary>
public sealed class TradingHub : Hub
{
    /// <summary>Nombres de los eventos server → client.</summary>
    public static class Events
    {
        public const string OnMarketTick       = nameof(OnMarketTick);
        public const string OnOrderExecuted    = nameof(OnOrderExecuted);
        public const string OnSignalGenerated  = nameof(OnSignalGenerated);
        public const string OnAlert            = nameof(OnAlert);
        public const string OnStrategyUpdated  = nameof(OnStrategyUpdated);
        public const string OnMetricsUpdate    = nameof(OnMetricsUpdate);
        public const string OnScannerUpdate    = nameof(OnScannerUpdate);
    }

    public override Task OnConnectedAsync()
    {
        var logger = Context.GetHttpContext()?.RequestServices.GetRequiredService<ILogger<TradingHub>>();
        logger?.LogInformation("Cliente SignalR conectado: {ConnectionId}", Context.ConnectionId);
        return base.OnConnectedAsync();
    }

    public override Task OnDisconnectedAsync(Exception? exception)
    {
        var logger = Context.GetHttpContext()?.RequestServices.GetRequiredService<ILogger<TradingHub>>();
        logger?.LogInformation("Cliente SignalR desconectado: {ConnectionId}", Context.ConnectionId);
        return base.OnDisconnectedAsync(exception);
    }
}
