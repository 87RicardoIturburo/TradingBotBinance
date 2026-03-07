using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Binance.Net.Enums;
using Binance.Net.Interfaces.Clients;
using CryptoExchange.Net.Objects.Sockets;
using Microsoft.Extensions.Logging;
using Polly;
using Polly.Retry;
using TradingBot.Core.Common;
using TradingBot.Core.Events;
using TradingBot.Core.Interfaces.Services;
using TradingBot.Core.ValueObjects;

namespace TradingBot.Infrastructure.Binance;

/// <summary>
/// Implementación de <see cref="IMarketDataService"/> usando Binance.Net.
/// <para>
/// WebSocket: un canal por símbolo; CryptoExchange.Net gestiona la reconexión
/// automática con backoff exponencial.
/// REST: Polly añade reintentos con backoff + jitter sobre los endpoints de datos.
/// </para>
/// </summary>
internal sealed class MarketDataService : IMarketDataService, IAsyncDisposable
{
    private readonly IBinanceRestClient              _restClient;
    private readonly IBinanceSocketClient            _socketClient;
    private readonly ILogger<MarketDataService>      _logger;
    private readonly ResiliencePipeline              _retryPipeline;

    private readonly ConcurrentDictionary<string, Channel<MarketTickReceivedEvent>> _channels     = new();
    private readonly ConcurrentDictionary<string, UpdateSubscription>               _subscriptions = new();

    private volatile bool _isConnected;
    public  bool IsConnected => _isConnected;

    public MarketDataService(
        IBinanceRestClient         restClient,
        IBinanceSocketClient       socketClient,
        ILogger<MarketDataService> logger)
    {
        _restClient   = restClient;
        _socketClient = socketClient;
        _logger       = logger;

        _retryPipeline = new ResiliencePipelineBuilder()
            .AddRetry(new RetryStrategyOptions
            {
                MaxRetryAttempts = 3,
                BackoffType      = DelayBackoffType.Exponential,
                UseJitter        = true,
                Delay            = TimeSpan.FromSeconds(1),
                OnRetry = args =>
                {
                    _logger.LogWarning(
                        "Reintentando llamada Binance REST (intento {Attempt}): {Error}",
                        args.AttemptNumber + 1,
                        args.Outcome.Exception?.Message);
                    return default;
                }
            })
            .AddTimeout(TimeSpan.FromSeconds(15))
            .Build();
    }

    // ── Suscripción WebSocket ─────────────────────────────────────────────

    public async Task SubscribeAsync(Symbol symbol, CancellationToken cancellationToken = default)
    {
        var sv = symbol.Value;

        if (_subscriptions.ContainsKey(sv))
        {
            _logger.LogDebug("Ya existe suscripción activa para {Symbol}", sv);
            return;
        }

        var channel = Channel.CreateUnbounded<MarketTickReceivedEvent>(
            new UnboundedChannelOptions { SingleReader = false, SingleWriter = true });

        _channels[sv] = channel;

        var result = await _socketClient.SpotApi.ExchangeData.SubscribeToTickerUpdatesAsync(
            sv,
            update =>
            {
                var d    = update.Data;
                var tick = BuildTick(sv, d.LastPrice, d.BestBidPrice, d.BestAskPrice, d.Volume);
                if (tick is not null)
                    channel.Writer.TryWrite(tick);
            },
            cancellationToken);

        if (!result.Success)
        {
            _channels.TryRemove(sv, out _);
            _logger.LogError("No se pudo suscribir a {Symbol}: {Error}", sv, result.Error?.Message);
            return;
        }

        result.Data.ConnectionClosed  += ()  => OnConnectionClosed(sv);
        result.Data.ConnectionRestored += ts => OnConnectionRestored(sv, ts);

        _subscriptions[sv] = result.Data;
        _isConnected        = true;

        _logger.LogInformation("Suscrito al stream de ticks de {Symbol} (Testnet: {IsTestnet})",
            sv, _socketClient.GetType().Name.Contains("Testnet"));
    }

    public async Task UnsubscribeAsync(Symbol symbol, CancellationToken cancellationToken = default)
    {
        var sv = symbol.Value;

        if (_subscriptions.TryRemove(sv, out var sub))
        {
            await _socketClient.UnsubscribeAsync(sub);
            _logger.LogInformation("Suscripción cancelada para {Symbol}", sv);
        }

        if (_channels.TryRemove(sv, out var ch))
            ch.Writer.TryComplete();

        if (_subscriptions.IsEmpty)
            _isConnected = false;
    }

    // ── Stream asíncrono de ticks ─────────────────────────────────────────

    public async IAsyncEnumerable<MarketTickReceivedEvent> GetTickStreamAsync(
        Symbol symbol,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (!_channels.TryGetValue(symbol.Value, out var channel))
        {
            _logger.LogWarning(
                "Sin canal de ticks para {Symbol}. Llama SubscribeAsync primero.", symbol.Value);
            yield break;
        }

        await foreach (var tick in channel.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            yield return tick;
    }

    // ── REST — precio actual ──────────────────────────────────────────────

    public async Task<Result<Price, DomainError>> GetCurrentPriceAsync(
        Symbol symbol,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var priceValue = await _retryPipeline.ExecuteAsync(async ct =>
            {
                var result = await _restClient.SpotApi.ExchangeData.GetPriceAsync(symbol.Value, ct: ct);

                if (!result.Success)
                    throw new InvalidOperationException(
                        result.Error?.Message ?? "Error desconocido al obtener precio de Binance.");

                return result.Data.Price;
            }, cancellationToken);

            return Price.Create(priceValue);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error al obtener precio actual de {Symbol}", symbol.Value);
            return Result<Price, DomainError>.Failure(DomainError.ExternalService(ex.Message));
        }
    }

    // ── REST — histórico de cierres ───────────────────────────────────────

    public async Task<Result<IReadOnlyList<decimal>, DomainError>> GetHistoricalClosesAsync(
        Symbol symbol,
        int    count,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var closes = await _retryPipeline.ExecuteAsync(async ct =>
            {
                var result = await _restClient.SpotApi.ExchangeData.GetKlinesAsync(
                    symbol.Value,
                    KlineInterval.OneMinute,
                    limit: count,
                    ct: ct);

                if (!result.Success)
                    throw new InvalidOperationException(
                        result.Error?.Message ?? "Error obteniendo histórico de Binance.");

                return result.Data.Select(k => k.ClosePrice).ToList();
            }, cancellationToken);

            return Result<IReadOnlyList<decimal>, DomainError>.Success(closes.AsReadOnly());
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error al obtener histórico de {Symbol}", symbol.Value);
            return Result<IReadOnlyList<decimal>, DomainError>.Failure(
                DomainError.ExternalService(ex.Message));
        }
    }

    // ── Helpers privados ──────────────────────────────────────────────────

    private static MarketTickReceivedEvent? BuildTick(
        string symbolStr,
        decimal lastPrice,
        decimal bidPrice,
        decimal askPrice,
        decimal volume)
    {
        var symbolResult = Symbol.Create(symbolStr);
        if (symbolResult.IsFailure) return null;

        var bid  = Price.Create(bidPrice);
        var ask  = Price.Create(askPrice);
        var last = Price.Create(lastPrice);

        if (bid.IsFailure || ask.IsFailure || last.IsFailure) return null;

        return new MarketTickReceivedEvent(
            Symbol:    symbolResult.Value,
            BidPrice:  bid.Value,
            AskPrice:  ask.Value,
            LastPrice: last.Value,
            Volume:    volume,
            Timestamp: DateTimeOffset.UtcNow);
    }

    private void OnConnectionClosed(string symbol)
    {
        _isConnected = false;
        _logger.LogWarning(
            "WebSocket desconectado para {Symbol}. CryptoExchange.Net reconectará automáticamente.", symbol);
    }

    private void OnConnectionRestored(string symbol, TimeSpan downtime)
    {
        _isConnected = true;
        _logger.LogInformation(
            "WebSocket reconectado para {Symbol}. Tiempo de caída: {Downtime:g}", symbol, downtime);
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var (_, ch) in _channels)
            ch.Writer.TryComplete();

        await _socketClient.UnsubscribeAllAsync();
        _logger.LogInformation("MarketDataService disposed — suscripciones WebSocket cerradas.");
    }
}
