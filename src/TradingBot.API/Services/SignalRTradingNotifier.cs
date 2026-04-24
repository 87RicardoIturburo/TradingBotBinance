using Microsoft.AspNetCore.SignalR;
using TradingBot.API.Hubs;
using TradingBot.Core.Events;
using TradingBot.Core.Interfaces.Services;

namespace TradingBot.API.Services;

/// <summary>
/// Implementación de <see cref="ITradingNotifier"/> que usa SignalR
/// para enviar eventos en tiempo real al frontend Blazor WASM.
/// </summary>
internal sealed class SignalRTradingNotifier(
    IHubContext<TradingHub> hubContext,
    ILogger<SignalRTradingNotifier> logger) : ITradingNotifier
{
    public async Task NotifyMarketTickAsync(
        MarketTickReceivedEvent tick, CancellationToken cancellationToken = default)
    {
        await hubContext.Clients.All.SendAsync(
            TradingHub.Events.OnMarketTick,
            new
            {
                Symbol    = tick.Symbol.Value,
                BidPrice  = tick.BidPrice.Value,
                AskPrice  = tick.AskPrice.Value,
                LastPrice = tick.LastPrice.Value,
                tick.Volume,
                tick.Timestamp
            },
            cancellationToken);
    }

    public async Task NotifyOrderExecutedAsync(
        OrderPlacedEvent order, CancellationToken cancellationToken = default)
    {
        logger.LogInformation(
            "Notificando orden ejecutada: {Side} {Symbol}", order.Side, order.Symbol.Value);

        await hubContext.Clients.All.SendAsync(
            TradingHub.Events.OnOrderExecuted,
            new
            {
                order.OrderId,
                order.StrategyId,
                Symbol = order.Symbol.Value,
                Side   = order.Side.ToString(),
                Type   = order.Type.ToString(),
                Quantity = order.Quantity.Value,
                order.IsPaperTrade
            },
            cancellationToken);
    }

    public async Task NotifySignalGeneratedAsync(
        SignalGeneratedEvent signal, CancellationToken cancellationToken = default)
    {
        await hubContext.Clients.All.SendAsync(
            TradingHub.Events.OnSignalGenerated,
            new
            {
                signal.StrategyId,
                Symbol    = signal.Symbol.Value,
                Direction = signal.Direction.ToString(),
                Price     = signal.CurrentPrice.Value,
                signal.IndicatorSnapshot
            },
            cancellationToken);
    }

    public async Task NotifyAlertAsync(
        string message, CancellationToken cancellationToken = default)
    {
        logger.LogWarning("Alerta enviada al frontend: {Message}", message);

        await hubContext.Clients.All.SendAsync(
            TradingHub.Events.OnAlert,
            message,
            cancellationToken);
    }

    public async Task NotifyStrategyUpdatedAsync(
        StrategyUpdatedEvent update, CancellationToken cancellationToken = default)
    {
        await hubContext.Clients.All.SendAsync(
            TradingHub.Events.OnStrategyUpdated,
            new
            {
                update.StrategyId,
                update.StrategyName,
                update.IsHotReload
            },
            cancellationToken);
    }

    public async Task NotifyScannerUpdateAsync(
        IReadOnlyList<SymbolScore> scores, CancellationToken cancellationToken = default)
    {
        await hubContext.Clients.All.SendAsync(
            TradingHub.Events.OnScannerUpdate,
            scores.Select(s => new
            {
                s.Symbol,
                s.Score,
                s.TrafficLight,
                s.Volume24hUsdt,
                s.SpreadPercent,
                s.AtrPercent,
                s.Regime,
                s.AdxValue,
                s.PriceChangePercent24h,
                s.ScannedAt
            }),
            cancellationToken);
    }

    public async Task NotifySymbolPoolUpdateAsync(
        SymbolPoolSnapshot snapshot, CancellationToken cancellationToken = default)
    {
        await hubContext.Clients.All.SendAsync(
            TradingHub.Events.OnSymbolPoolUpdate,
            new
            {
                snapshot.Enabled,
                snapshot.EvaluatedCount,
                snapshot.BlockedByRegime,
                snapshot.BlockedByScore,
                snapshot.BlockedByCooldown,
                snapshot.ActiveCount,
                snapshot.ZombiesRemoved,
                Items = snapshot.Items.Select(i => new
                {
                    i.Symbol,
                    i.Score,
                    i.Regime,
                    i.IsActive,
                    i.AllowNewEntries,
                    i.BlockReason,
                    i.RegimeStability
                }),
                snapshot.Timestamp
            },
            cancellationToken);
    }
}
