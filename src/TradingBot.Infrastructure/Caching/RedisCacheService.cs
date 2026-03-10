using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using StackExchange.Redis;
using TradingBot.Core.Interfaces.Services;

namespace TradingBot.Infrastructure.Caching;

/// <summary>
/// Implementación de <see cref="ICacheService"/> con StackExchange.Redis.
/// Usa JSON para serialización y soporta expiración configurable.
/// </summary>
internal sealed class RedisCacheService : ICacheService, IAsyncDisposable
{
    private readonly IConnectionMultiplexer      _connection;
    private readonly IDatabase                   _database;
    private readonly ILogger<RedisCacheService>  _logger;
    private readonly string                      _keyPrefix;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy        = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    public RedisCacheService(
        IConnectionMultiplexer      connection,
        IOptions<RedisOptions>      options,
        ILogger<RedisCacheService>  logger)
    {
        _connection = connection;
        _database   = connection.GetDatabase();
        _logger     = logger;
        _keyPrefix  = options.Value.KeyPrefix;
    }

    public async Task<T?> GetAsync<T>(string key, CancellationToken cancellationToken = default)
        where T : class
    {
        RedisValue value;
        var fullKey = BuildKey(key);

        try
        {
            value = await _database.StringGetAsync(fullKey);
        }
        catch (RedisException ex)
        {
            _logger.LogWarning(ex, "Error de Redis al leer {Key}; se trata como MISS", fullKey);
            return null;
        }

        if (value.IsNullOrEmpty)
        {
            _logger.LogDebug("Cache MISS para {Key}", fullKey);
            return null;
        }

        try
        {
            _logger.LogDebug("Cache HIT para {Key}", fullKey);
            return JsonSerializer.Deserialize<T>((string)value!, JsonOptions);
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex,
                "No se pudo deserializar {Key} como {Type}; se elimina la entrada corrupta",
                fullKey, typeof(T).Name);
            await _database.KeyDeleteAsync(fullKey);
            return null;
        }
        catch (NotSupportedException ex)
        {
            _logger.LogWarning(ex,
                "El tipo {Type} no es compatible con la deserialización JSON; se elimina {Key}",
                typeof(T).Name, fullKey);
            await _database.KeyDeleteAsync(fullKey);
            return null;
        }
    }

    public async Task SetAsync<T>(
        string key,
        T value,
        TimeSpan? expiration = null,
        CancellationToken cancellationToken = default)
        where T : class
    {
        var fullKey = BuildKey(key);

        try
        {
            var serialized = JsonSerializer.Serialize(value, JsonOptions);

            if (expiration.HasValue)
                await _database.StringSetAsync(fullKey, serialized, new Expiration(expiration.Value));
            else
                await _database.StringSetAsync(fullKey, serialized);

            _logger.LogDebug("Cache SET {Key} (TTL: {Expiration})", fullKey, expiration?.ToString() ?? "∞");
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException or RedisException)
        {
            _logger.LogWarning(ex, "Error al escribir en caché {Key} para {Type}; operación ignorada",
                fullKey, typeof(T).Name);
        }
    }

    public async Task RemoveAsync(string key, CancellationToken cancellationToken = default)
    {
        var fullKey = BuildKey(key);
        await _database.KeyDeleteAsync(fullKey);
        _logger.LogDebug("Cache DEL {Key}", fullKey);
    }

    public async Task<bool> ExistsAsync(string key, CancellationToken cancellationToken = default)
    {
        var fullKey = BuildKey(key);
        return await _database.KeyExistsAsync(fullKey);
    }

    private string BuildKey(string key) => $"{_keyPrefix}{key}";

    public async ValueTask DisposeAsync()
    {
        if (_connection is IAsyncDisposable disposable)
            await disposable.DisposeAsync();
    }
}
