namespace TradingBot.Core.Interfaces.Services;

/// <summary>
/// Abstracción de caché distribuida para estrategias activas y precios.
/// La implementación usa Redis (StackExchange.Redis).
/// </summary>
public interface ICacheService
{
    /// <summary>Obtiene un valor deserializado del caché.</summary>
    Task<T?> GetAsync<T>(string key, CancellationToken cancellationToken = default) where T : class;

    /// <summary>Almacena un valor serializado en el caché con expiración opcional.</summary>
    Task SetAsync<T>(string key, T value, TimeSpan? expiration = null, CancellationToken cancellationToken = default) where T : class;

    /// <summary>Elimina una entrada del caché.</summary>
    Task RemoveAsync(string key, CancellationToken cancellationToken = default);

    /// <summary>Verifica si una clave existe en el caché.</summary>
    Task<bool> ExistsAsync(string key, CancellationToken cancellationToken = default);
}
