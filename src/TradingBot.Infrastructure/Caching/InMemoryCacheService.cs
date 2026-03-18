using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using TradingBot.Core.Interfaces.Services;

namespace TradingBot.Infrastructure.Caching;

/// <summary>
/// IMP-6: Implementación de <see cref="ICacheService"/> en memoria usando <see cref="ConcurrentDictionary{TKey,TValue}"/>.
/// Se usa como fallback cuando Redis no está disponible.
/// Soporta TTL via un timer periódico que limpia entradas expiradas.
/// </summary>
internal sealed class InMemoryCacheService : ICacheService, IDisposable
{
    private readonly ConcurrentDictionary<string, CacheEntry> _store = new();
    private readonly Timer _cleanupTimer;
    private readonly ILogger<InMemoryCacheService> _logger;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy        = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    public InMemoryCacheService(ILogger<InMemoryCacheService> logger)
    {
        _logger = logger;
        // Limpiar entradas expiradas cada 60 segundos
        _cleanupTimer = new Timer(CleanupExpired, null, TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(60));
        _logger.LogWarning("Usando InMemoryCacheService como fallback — los datos no sobreviven reinicios");
    }

    public Task<T?> GetAsync<T>(string key, CancellationToken cancellationToken = default)
        where T : class
    {
        if (!_store.TryGetValue(key, out var entry))
            return Task.FromResult<T?>(null);

        if (entry.ExpiresAt.HasValue && entry.ExpiresAt.Value < DateTimeOffset.UtcNow)
        {
            _store.TryRemove(key, out _);
            return Task.FromResult<T?>(null);
        }

        try
        {
            var result = JsonSerializer.Deserialize<T>(entry.Json, JsonOptions);
            return Task.FromResult(result);
        }
        catch (JsonException)
        {
            _store.TryRemove(key, out _);
            return Task.FromResult<T?>(null);
        }
    }

    public Task SetAsync<T>(
        string key,
        T value,
        TimeSpan? expiration = null,
        CancellationToken cancellationToken = default)
        where T : class
    {
        var json = JsonSerializer.Serialize(value, JsonOptions);
        var expiresAt = expiration.HasValue
            ? DateTimeOffset.UtcNow + expiration.Value
            : (DateTimeOffset?)null;

        _store[key] = new CacheEntry(json, expiresAt);
        return Task.CompletedTask;
    }

    public Task RemoveAsync(string key, CancellationToken cancellationToken = default)
    {
        _store.TryRemove(key, out _);
        return Task.CompletedTask;
    }

    public Task<bool> ExistsAsync(string key, CancellationToken cancellationToken = default)
    {
        if (!_store.TryGetValue(key, out var entry))
            return Task.FromResult(false);

        if (entry.ExpiresAt.HasValue && entry.ExpiresAt.Value < DateTimeOffset.UtcNow)
        {
            _store.TryRemove(key, out _);
            return Task.FromResult(false);
        }

        return Task.FromResult(true);
    }

    private void CleanupExpired(object? state)
    {
        var now = DateTimeOffset.UtcNow;
        var removed = 0;

        foreach (var kvp in _store)
        {
            if (kvp.Value.ExpiresAt.HasValue && kvp.Value.ExpiresAt.Value < now)
            {
                if (_store.TryRemove(kvp.Key, out _))
                    removed++;
            }
        }

        if (removed > 0)
            _logger.LogDebug("InMemoryCache: {Removed} entradas expiradas eliminadas", removed);
    }

    public void Dispose() => _cleanupTimer.Dispose();

    private sealed record CacheEntry(string Json, DateTimeOffset? ExpiresAt);
}
