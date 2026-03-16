using global::Binance.Net.Interfaces.Clients;
using global::Binance.Net.Objects.Models.Spot;
using Microsoft.Extensions.Logging;
using Polly;
using Polly.Retry;
using TradingBot.Core.Common;
using TradingBot.Core.Interfaces.Services;
using TradingBot.Core.ValueObjects;

namespace TradingBot.Infrastructure.Binance;

/// <summary>
/// Obtiene y cachea los filtros de exchange de Binance por símbolo.
/// Usa Redis con TTL de 1 hora — los filtros de Binance cambian raramente.
/// </summary>
internal sealed class BinanceExchangeInfoService : IExchangeInfoService
{
    private const string CacheKeyPrefix = "exchange:filters:";
    private static readonly TimeSpan CacheTtl = TimeSpan.FromHours(1);

    private readonly IBinanceRestClient            _restClient;
    private readonly ICacheService                 _cache;
    private readonly ILogger<BinanceExchangeInfoService> _logger;
    private readonly ResiliencePipeline            _retryPipeline;

    public BinanceExchangeInfoService(
        IBinanceRestClient                 restClient,
        ICacheService                      cache,
        ILogger<BinanceExchangeInfoService> logger)
    {
        _restClient = restClient;
        _cache      = cache;
        _logger     = logger;

        _retryPipeline = new ResiliencePipelineBuilder()
            .AddRetry(new RetryStrategyOptions
            {
                MaxRetryAttempts = 3,
                BackoffType      = DelayBackoffType.Exponential,
                UseJitter        = true,
                Delay            = TimeSpan.FromSeconds(1),
            })
            .AddTimeout(TimeSpan.FromSeconds(15))
            .Build();
    }

    public async Task<Result<ExchangeSymbolFilters, DomainError>> GetFiltersAsync(
        string            symbol,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(symbol);

        var cacheKey = CacheKeyPrefix + symbol.ToUpperInvariant();

        // Cache hit
        var cached = await _cache.GetAsync<ExchangeSymbolFilters>(cacheKey, cancellationToken);
        if (cached is not null)
        {
            _logger.LogDebug("Filtros de exchange para {Symbol} obtenidos del caché", symbol);
            return Result<ExchangeSymbolFilters, DomainError>.Success(cached);
        }

        // Cache miss → llamar a Binance REST
        _logger.LogInformation("Obteniendo filtros de exchange para {Symbol} desde Binance", symbol);

        try
        {
            var response = await _retryPipeline.ExecuteAsync(async ct =>
                await _restClient.SpotApi.ExchangeData.GetExchangeInfoAsync(symbol, null, ct),
                cancellationToken);

            if (!response.Success || response.Data is null)
            {
                _logger.LogWarning(
                    "Binance no devolvió exchange info para {Symbol}: {Error}",
                    symbol, response.Error?.Message);
                return Result<ExchangeSymbolFilters, DomainError>.Failure(
                    DomainError.ExternalService(
                        $"No se pudo obtener exchange info para {symbol}: {response.Error?.Message}"));
            }

            var symbolInfo = response.Data.Symbols.FirstOrDefault(s =>
                string.Equals(s.Name, symbol, StringComparison.OrdinalIgnoreCase));

            if (symbolInfo is null)
                return Result<ExchangeSymbolFilters, DomainError>.Failure(
                    DomainError.NotFound($"Símbolo '{symbol}' no encontrado en Binance."));

            var filters = ParseFilters(symbolInfo);

            await _cache.SetAsync(cacheKey, filters, CacheTtl, cancellationToken);
            _logger.LogInformation(
                "Filtros de exchange para {Symbol}: MinQty={MinQty} StepSize={Step} TickSize={Tick} MinNotional={MinNotional}",
                symbol, filters.MinQty, filters.StepSize, filters.TickSize, filters.MinNotional);

            return Result<ExchangeSymbolFilters, DomainError>.Success(filters);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error obteniendo filtros de exchange para {Symbol}", symbol);
            return Result<ExchangeSymbolFilters, DomainError>.Failure(
                DomainError.ExternalService($"Error consultando exchange info: {ex.Message}"));
        }
    }

    public async Task InvalidateCacheAsync(string symbol, CancellationToken cancellationToken = default)
    {
        var cacheKey = CacheKeyPrefix + symbol.ToUpperInvariant();
        await _cache.RemoveAsync(cacheKey, cancellationToken);
        _logger.LogDebug("Caché de filtros invalidada para {Symbol}", symbol);
    }

    private static ExchangeSymbolFilters ParseFilters(BinanceSymbol info)
    {
        decimal minQty       = 0m;
        decimal maxQty       = decimal.MaxValue;
        decimal stepSize     = 0m;
        decimal tickSize     = 0m;
        decimal minNotional  = 0m;
        int     maxNumOrders = 200;

        foreach (var filter in info.Filters)
        {
            switch (filter)
            {
                case BinanceSymbolLotSizeFilter lot:
                    minQty   = lot.MinQuantity;
                    maxQty   = lot.MaxQuantity;
                    stepSize = lot.StepSize;
                    break;

                case BinanceSymbolPriceFilter price:
                    tickSize = price.TickSize;
                    break;

                case BinanceSymbolMinNotionalFilter notional:
                    minNotional = notional.MinNotional;
                    break;

                case BinanceSymbolNotionalFilter notional2:
                    minNotional = Math.Max(minNotional, notional2.MinNotional);
                    break;

                case BinanceSymbolMaxOrdersFilter orders:
                    maxNumOrders = orders.MaxNumberOrders;
                    break;
            }
        }

        return new ExchangeSymbolFilters(
            Symbol:      info.Name,
            MinQty:      minQty,
            MaxQty:      maxQty,
            StepSize:    stepSize,
            TickSize:    tickSize,
            MinNotional: minNotional,
            MaxNumOrders: maxNumOrders);
    }
}
