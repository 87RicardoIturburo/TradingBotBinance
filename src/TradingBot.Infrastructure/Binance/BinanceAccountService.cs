using global::Binance.Net.Interfaces.Clients;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Polly;
using Polly.Retry;
using TradingBot.Core.Common;
using TradingBot.Core.Interfaces.Services;
using TradingBot.Core.ValueObjects;

namespace TradingBot.Infrastructure.Binance;

/// <summary>Wrapper para cachear un decimal vía Redis (ICacheService requiere T : class).</summary>
internal sealed record CachedDecimal(decimal Value);

/// <summary>
/// Consulta el balance de la cuenta Binance vía REST con caché de 5 segundos.
/// Expone sólo la información necesaria — nunca las API Keys.
/// </summary>
internal sealed class BinanceAccountService : IAccountService
{
    private const string SnapshotCacheKey    = "account:snapshot";
    private const string BalanceCachePrefix  = "account:balance:";
    private static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(5);

    private readonly IBinanceRestClient          _restClient;
    private readonly ICacheService               _cache;
    private readonly bool                        _hasCredentials;
    private readonly ILogger<BinanceAccountService> _logger;
    private readonly ResiliencePipeline          _retryPipeline;

    public BinanceAccountService(
        IBinanceRestClient             restClient,
        ICacheService                  cache,
        IOptions<BinanceOptions>       options,
        ILogger<BinanceAccountService> logger)
    {
        _restClient      = restClient;
        _cache           = cache;
        _hasCredentials  = options.Value.HasCredentials;
        _logger          = logger;

        _retryPipeline = new ResiliencePipelineBuilder()
            .AddRetry(new RetryStrategyOptions
            {
                MaxRetryAttempts = 2,
                BackoffType      = DelayBackoffType.Exponential,
                UseJitter        = true,
                Delay            = TimeSpan.FromMilliseconds(300),
            })
            .AddTimeout(TimeSpan.FromSeconds(10))
            .Build();
    }

    public async Task<Result<decimal, DomainError>> GetAvailableBalanceAsync(
        string            asset,
        CancellationToken cancellationToken = default)
    {
        if (!_hasCredentials)
            return Result<decimal, DomainError>.Failure(
                DomainError.ExternalService("No hay API keys configuradas — balance no disponible."));

        ArgumentException.ThrowIfNullOrWhiteSpace(asset);

        var normalizedAsset = asset.ToUpperInvariant();
        var cacheKey = BalanceCachePrefix + normalizedAsset;

        var cached = await _cache.GetAsync<CachedDecimal>(cacheKey, cancellationToken);
        if (cached is not null)
            return Result<decimal, DomainError>.Success(cached.Value);

        var snapshotResult = await GetAccountSnapshotAsync(cancellationToken);
        if (snapshotResult.IsFailure)
            return Result<decimal, DomainError>.Failure(snapshotResult.Error);

        var balance = snapshotResult.Value
            .FirstOrDefault(b => b.Asset == normalizedAsset)?.Free ?? 0m;

        await _cache.SetAsync(cacheKey, new CachedDecimal(balance), CacheTtl, cancellationToken);

        return Result<decimal, DomainError>.Success(balance);
    }

    public async Task<Result<IReadOnlyList<AccountBalance>, DomainError>> GetAccountSnapshotAsync(
        CancellationToken cancellationToken = default)
    {
        if (!_hasCredentials)
            return Result<IReadOnlyList<AccountBalance>, DomainError>.Failure(
                DomainError.ExternalService("No hay API keys configuradas — balance no disponible."));
        var cached = await _cache.GetAsync<List<AccountBalance>>(SnapshotCacheKey, cancellationToken);
        if (cached is not null)
            return Result<IReadOnlyList<AccountBalance>, DomainError>.Success(cached);

        try
        {
            var response = await _retryPipeline.ExecuteAsync(async ct =>
                await _restClient.SpotApi.Account.GetAccountInfoAsync(ct: ct),
                cancellationToken);

            if (!response.Success || response.Data is null)
            {
                _logger.LogWarning("No se pudo obtener balance de Binance: {Error}", response.Error?.Message);
                return Result<IReadOnlyList<AccountBalance>, DomainError>.Failure(
                    DomainError.ExternalService(
                        $"No se pudo obtener balance de Binance: {response.Error?.Message}"));
            }

            var balances = response.Data.Balances
                .Where(b => b.Total > 0)
                .Select(b => new AccountBalance(b.Asset, b.Available, b.Locked))
                .ToList();

            await _cache.SetAsync(SnapshotCacheKey, balances, CacheTtl, cancellationToken);

            _logger.LogDebug(
                "Balance obtenido de Binance: {Count} assets con saldo",
                balances.Count);

            return Result<IReadOnlyList<AccountBalance>, DomainError>.Success(balances);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error consultando balance de Binance");
            return Result<IReadOnlyList<AccountBalance>, DomainError>.Failure(
                DomainError.ExternalService($"Error consultando balance: {ex.Message}"));
        }
    }

    public async Task InvalidateCacheAsync(CancellationToken cancellationToken = default)
    {
        await _cache.RemoveAsync(SnapshotCacheKey, cancellationToken);
        _logger.LogDebug("Caché de balance de cuenta invalidada");
    }
}
