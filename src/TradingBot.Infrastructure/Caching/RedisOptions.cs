namespace TradingBot.Infrastructure.Caching;

/// <summary>
/// Configuración de conexión a Redis.
/// Se lee desde la sección <c>Redis</c> de appsettings.json o la variable de entorno <c>REDIS_CONNECTION</c>.
/// </summary>
public sealed class RedisOptions
{
    public const string SectionName = "Redis";

    /// <summary>Cadena de conexión a Redis (ej: <c>localhost:6379</c>).</summary>
    public string ConnectionString { get; set; } = "localhost:6379";

    /// <summary>Prefijo que se antepone a todas las claves para aislar el namespace.</summary>
    public string KeyPrefix { get; set; } = "tradingbot:";
}
