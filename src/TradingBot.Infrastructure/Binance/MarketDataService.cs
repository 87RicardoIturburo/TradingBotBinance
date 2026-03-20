using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Binance.Net.Clients;
using Binance.Net.Enums;
using Binance.Net.Interfaces.Clients;
using CryptoExchange.Net.Objects.Sockets;
using Microsoft.Extensions.Logging;
using Polly;
using Polly.Retry;
using TradingBot.Application.Diagnostics;
using TradingBot.Core.Common;
using TradingBot.Core.Enums;
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
    private readonly TradingMetrics                  _metrics;
    private readonly ResiliencePipeline              _retryPipeline;

    private readonly ConcurrentDictionary<string, Channel<MarketTickReceivedEvent>> _channels     = new();
    private readonly ConcurrentDictionary<string, Channel<KlineClosedEvent>>       _klineChannels = new();
    private readonly ConcurrentDictionary<string, UpdateSubscription>               _subscriptions = new();
    private readonly ConcurrentDictionary<string, UpdateSubscription>               _klineSubscriptions = new();
    private readonly ConcurrentDictionary<string, (Price Bid, Price Ask)>           _lastBidAsk    = new();

    private volatile bool _isConnected;
    public  bool IsConnected => _isConnected;

    public MarketDataService(
        IBinanceRestClient         restClient,
        IBinanceSocketClient       socketClient,
        ILogger<MarketDataService> logger,
        TradingMetrics             metrics)
    {
        _restClient   = restClient;
        _socketClient = socketClient;
        _logger       = logger;
        _metrics      = metrics;

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

        var channel = Channel.CreateBounded<MarketTickReceivedEvent>(
            new BoundedChannelOptions(1000)
            {
                SingleReader = false,
                SingleWriter = true,
                FullMode = BoundedChannelFullMode.DropOldest
            });

        _channels[sv] = channel;

        var result = await _socketClient.SpotApi.ExchangeData.SubscribeToTickerUpdatesAsync(
            sv,
            update =>
            {
                var d    = update.Data;
                var tick = BuildTick(sv, d.LastPrice, d.BestBidPrice, d.BestAskPrice, d.Volume);
                if (tick is not null)
                {
                    // Almacenar último bid/ask para spread guard
                    _lastBidAsk[sv] = (tick.BidPrice, tick.AskPrice);
                    if (!channel.Writer.TryWrite(tick))
                        _metrics.RecordTickDropped(sv, "ticker");
                }
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

    // ── Suscripción Kline WebSocket ───────────────────────────────────────

    public async Task SubscribeKlinesAsync(Symbol symbol, CandleInterval interval, CancellationToken cancellationToken = default)
    {
        var sv = symbol.Value;
        var key = $"{sv}_{interval}";

        if (_klineSubscriptions.ContainsKey(key))
        {
            _logger.LogDebug("Ya existe suscripción kline activa para {Key}", key);
            return;
        }

        var channel = Channel.CreateBounded<KlineClosedEvent>(
            new BoundedChannelOptions(500)
            {
                SingleReader = false,
                SingleWriter = true,
                FullMode = BoundedChannelFullMode.DropOldest
            });

        _klineChannels[key] = channel;

        var binanceInterval = MapInterval(interval);

        var result = await _socketClient.SpotApi.ExchangeData.SubscribeToKlineUpdatesAsync(
            sv,
            binanceInterval,
            update =>
            {
                var d = update.Data.Data;
                // Solo emitir cuando la vela se cierra (Final == true)
                if (!d.Final) return;

                var symbolResult = Symbol.Create(sv);
                if (symbolResult.IsFailure) return;

                var klineEvent = new KlineClosedEvent(
                    symbolResult.Value,
                    interval,
                    d.OpenPrice,
                    d.HighPrice,
                    d.LowPrice,
                    d.ClosePrice,
                    d.Volume,
                    d.OpenTime,
                    d.CloseTime);

                if (!channel.Writer.TryWrite(klineEvent))
                    _metrics.RecordTickDropped(sv, "kline");
            },
            cancellationToken);

        if (!result.Success)
        {
            _klineChannels.TryRemove(sv, out _);
            _logger.LogError("No se pudo suscribir a klines de {Symbol} [{Interval}]: {Error}",
                sv, interval, result.Error?.Message);
            return;
        }

        _klineSubscriptions[key] = result.Data;

        _logger.LogInformation(
            "Suscrito al stream de klines de {Symbol} [{Interval}]", sv, interval);
    }

    public async IAsyncEnumerable<KlineClosedEvent> GetKlineStreamAsync(
        Symbol symbol,
        CandleInterval interval,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var key = $"{symbol.Value}_{interval}";
        if (!_klineChannels.TryGetValue(key, out var channel))
        {
            _logger.LogWarning(
                "Sin canal de klines para {Key}. Llama SubscribeKlinesAsync primero.", key);
            yield break;
        }

        await foreach (var kline in channel.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            yield return kline;
    }

    private static KlineInterval MapInterval(CandleInterval interval) => interval switch
    {
        CandleInterval.OneMinute      => KlineInterval.OneMinute,
        CandleInterval.FiveMinutes    => KlineInterval.FiveMinutes,
        CandleInterval.FifteenMinutes => KlineInterval.FifteenMinutes,
        CandleInterval.ThirtyMinutes  => KlineInterval.ThirtyMinutes,
        CandleInterval.OneHour        => KlineInterval.OneHour,
        CandleInterval.FourHours      => KlineInterval.FourHour,
        CandleInterval.OneDay         => KlineInterval.OneDay,
        _ => KlineInterval.OneMinute
    };

    // ── REST — precio actual ──────────────────────────────────────────────

    /// <inheritdoc />
    public (Price Bid, Price Ask)? GetLastBidAsk(Symbol symbol)
        => _lastBidAsk.TryGetValue(symbol.Value, out var pair) ? pair : null;

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
            var closes = await FetchClosesFromClientAsync(_restClient, symbol, count, cancellationToken);

            if (closes.Count == 0)
            {
                _logger.LogWarning(
                    "Entorno actual retornó 0 cierres para {Symbol}. Intentando fallback con producción…",
                    symbol.Value);

                closes = await FetchClosesFromProductionAsync(symbol, count, cancellationToken);

                if (closes.Count > 0)
                    _logger.LogInformation(
                        "Fallback exitoso: {Count} cierres de producción para {Symbol}",
                        closes.Count, symbol.Value);
            }

            return Result<IReadOnlyList<decimal>, DomainError>.Success(closes.AsReadOnly());
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error al obtener histórico de {Symbol}", symbol.Value);
            return Result<IReadOnlyList<decimal>, DomainError>.Failure(
                DomainError.ExternalService(ex.Message));
        }
    }

    private async Task<List<decimal>> FetchClosesFromClientAsync(
        IBinanceRestClient client,
        Symbol symbol,
        int count,
        CancellationToken cancellationToken)
    {
        return await _retryPipeline.ExecuteAsync(async ct =>
        {
            var result = await client.SpotApi.ExchangeData.GetKlinesAsync(
                symbol.Value,
                KlineInterval.OneMinute,
                limit: count,
                ct: ct);

            if (!result.Success)
                throw new InvalidOperationException(
                    result.Error?.Message ?? "Error obteniendo histórico de Binance.");

            return result.Data.Select(k => k.ClosePrice).ToList();
        }, cancellationToken);
    }

    private async Task<List<decimal>> FetchClosesFromProductionAsync(
        Symbol symbol,
        int count,
        CancellationToken cancellationToken)
    {
        try
        {
            using var productionClient = new BinanceRestClient();
            return await FetchClosesFromClientAsync(
                productionClient, symbol, count, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Fallback a producción también falló para cierres de {Symbol}.",
                symbol.Value);
            return [];
        }
    }

    // ── REST — klines para backtesting ────────────────────────────────────

    public async Task<Result<IReadOnlyList<Kline>, DomainError>> GetKlinesAsync(
        Symbol symbol,
        DateTimeOffset from,
        DateTimeOffset to,
        CandleInterval interval = CandleInterval.OneMinute,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var allKlines = await FetchKlinesFromClientAsync(
                _restClient, symbol, from, to, interval, cancellationToken);

            if (allKlines.Count == 0)
            {
                _logger.LogWarning(
                    "Entorno actual retornó 0 klines para {Symbol}. Intentando fallback con producción Binance…",
                    symbol.Value);

                allKlines = await FetchKlinesFromProductionAsync(
                    symbol, from, to, interval, cancellationToken);

                if (allKlines.Count > 0)
                    _logger.LogInformation(
                        "Fallback exitoso: {Count} klines de producción para {Symbol}",
                        allKlines.Count, symbol.Value);
            }

            _logger.LogInformation(
                "Klines descargadas: {Total} velas de {Symbol} ({From} → {To})",
                allKlines.Count, symbol.Value, from, to);

            return Result<IReadOnlyList<Kline>, DomainError>.Success(allKlines.AsReadOnly());
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error al obtener klines de {Symbol}", symbol.Value);
            return Result<IReadOnlyList<Kline>, DomainError>.Failure(
                DomainError.ExternalService(ex.Message));
        }
    }

    private async Task<List<Kline>> FetchKlinesFromClientAsync(
        IBinanceRestClient client,
        Symbol symbol,
        DateTimeOffset from,
        DateTimeOffset to,
        CandleInterval interval,
        CancellationToken cancellationToken)
    {
        var allKlines = new List<Kline>();
        var currentFrom = from;
        var binanceInterval = MapInterval(interval);

        while (currentFrom < to)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var batch = await _retryPipeline.ExecuteAsync(async ct =>
            {
                var result = await client.SpotApi.ExchangeData.GetKlinesAsync(
                    symbol.Value,
                    binanceInterval,
                    startTime: currentFrom.UtcDateTime,
                    endTime: to.UtcDateTime,
                    limit: 1000,
                    ct: ct);

                if (!result.Success)
                    throw new InvalidOperationException(
                        result.Error?.Message ?? "Error obteniendo klines de Binance.");

                return result.Data
                    .Select(k => new Kline(k.OpenTime, k.OpenPrice, k.HighPrice, k.LowPrice, k.ClosePrice, k.Volume))
                    .ToList();
            }, cancellationToken);

            if (batch.Count == 0)
                break;

            allKlines.AddRange(batch);
            currentFrom = batch[^1].OpenTime.AddMinutes(1);

            _logger.LogDebug(
                "Klines batch: {Count} velas para {Symbol} hasta {Until}",
                batch.Count, symbol.Value, currentFrom);
        }

        return allKlines;
    }

    /// <summary>
    /// Fallback: descarga klines desde producción Binance (datos públicos, sin auth).
    /// Se usa cuando el entorno Demo/Testnet no retorna datos históricos.
    /// </summary>
    private async Task<List<Kline>> FetchKlinesFromProductionAsync(
        Symbol symbol,
        DateTimeOffset from,
        DateTimeOffset to,
        CandleInterval interval,
        CancellationToken cancellationToken)
    {
        try
        {
            using var productionClient = new BinanceRestClient();
            return await FetchKlinesFromClientAsync(
                productionClient, symbol, from, to, interval, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Fallback a producción también falló para {Symbol}. Los indicadores arrancarán sin warm-up.",
                symbol.Value);
            return [];
        }
    }

    // ── REST — symbols del exchange ───────────────────────────────────────

    public async Task<Result<IReadOnlyList<TradingSymbolInfo>, DomainError>> GetTradingSymbolsAsync(
        string quoteAsset = "USDT",
        CancellationToken cancellationToken = default)
    {
        try
        {
            var symbols = await _retryPipeline.ExecuteAsync(async ct =>
            {
                var result = await _restClient.SpotApi.ExchangeData.GetExchangeInfoAsync(ct: ct);

                if (!result.Success)
                    throw new InvalidOperationException(
                        result.Error?.Message ?? "Error obteniendo exchange info de Binance.");

                return result.Data.Symbols
                    .Where(s => s.Status == SymbolStatus.Trading
                             && s.QuoteAsset.Equals(quoteAsset, StringComparison.OrdinalIgnoreCase))
                    .Select(s => new TradingSymbolInfo(s.Name, s.BaseAsset, s.QuoteAsset))
                    .OrderBy(s => s.Symbol)
                    .ToList();
            }, cancellationToken);

            _logger.LogInformation(
                "Symbols cargados: {Count} pares con quote {Quote}",
                symbols.Count, quoteAsset);

            return Result<IReadOnlyList<TradingSymbolInfo>, DomainError>.Success(symbols.AsReadOnly());
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error al obtener symbols de Binance");
            return Result<IReadOnlyList<TradingSymbolInfo>, DomainError>.Failure(
                DomainError.ExternalService(ex.Message));
        }
    }

    // ── Helpers privados ──────────────────────────────────────────────────

    public async Task<Result<IReadOnlyList<Ticker24h>, DomainError>> Get24hTickersAsync(
        CancellationToken cancellationToken = default)
    {
        try
        {
            var tickers = await _retryPipeline.ExecuteAsync(async ct =>
            {
                var result = await _restClient.SpotApi.ExchangeData.GetTickersAsync(ct: ct);

                if (!result.Success)
                    throw new InvalidOperationException(
                        result.Error?.Message ?? "Error obteniendo tickers 24h de Binance.");

                return result.Data
                    .Select(t => new Ticker24h(
                        t.Symbol,
                        t.LastPrice,
                        t.BestBidPrice,
                        t.BestAskPrice,
                        t.Volume,
                        t.QuoteVolume,
                        t.PriceChangePercent,
                        t.HighPrice,
                        t.LowPrice))
                    .ToList();
            }, cancellationToken);

            return Result<IReadOnlyList<Ticker24h>, DomainError>.Success(tickers.AsReadOnly());
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error al obtener tickers 24h de Binance");
            return Result<IReadOnlyList<Ticker24h>, DomainError>.Failure(
                DomainError.ExternalService(ex.Message));
        }
    }

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
