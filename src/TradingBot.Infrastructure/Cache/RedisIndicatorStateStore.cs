using System.Text.Json;
using Microsoft.Extensions.Logging;
using TradingBot.Core.Enums;
using TradingBot.Core.Interfaces.Services;

namespace TradingBot.Infrastructure.Cache;

/// <summary>
/// Implementación de <see cref="IIndicatorStateStore"/> que persiste el estado
/// de los indicadores técnicos en Redis vía <see cref="ICacheService"/>.
/// Key: <c>indicator:{strategyId}</c>, TTL: 24 horas.
/// </summary>
internal sealed class RedisIndicatorStateStore(
    ICacheService cacheService,
    ILogger<RedisIndicatorStateStore> logger) : IIndicatorStateStore
{
    private static readonly TimeSpan Ttl = TimeSpan.FromHours(24);

    public async Task SaveAsync(
        Guid strategyId,
        IReadOnlyDictionary<IndicatorType, string> states,
        CancellationToken cancellationToken = default)
    {
        var key = BuildKey(strategyId);
        var wrapper = new IndicatorStatesWrapper
        {
            States = states.ToDictionary(kv => kv.Key.ToString(), kv => kv.Value),
            SavedAtUtc = DateTimeOffset.UtcNow
        };

        await cacheService.SetAsync(key, wrapper, Ttl, cancellationToken);

        logger.LogDebug(
            "Estado de {Count} indicadores guardado para estrategia {StrategyId}",
            states.Count, strategyId);
    }

    public async Task<IReadOnlyDictionary<IndicatorType, string>?> RestoreAsync(
        Guid strategyId,
        CancellationToken cancellationToken = default)
    {
        var key = BuildKey(strategyId);
        var wrapper = await cacheService.GetAsync<IndicatorStatesWrapper>(key, cancellationToken);

        if (wrapper is null)
            return null;

        var result = new Dictionary<IndicatorType, string>();
        foreach (var (typeStr, json) in wrapper.States)
        {
            if (Enum.TryParse<IndicatorType>(typeStr, out var type))
                result[type] = json;
        }

        logger.LogDebug(
            "Estado de {Count} indicadores restaurado para estrategia {StrategyId} (guardado {Ago} atrás)",
            result.Count, strategyId, DateTimeOffset.UtcNow - wrapper.SavedAtUtc);

        return result;
    }

    public async Task RemoveAsync(Guid strategyId, CancellationToken cancellationToken = default)
    {
        var key = BuildKey(strategyId);
        await cacheService.RemoveAsync(key, cancellationToken);
    }

    private static string BuildKey(Guid strategyId) => $"indicator:{strategyId}";

    /// <summary>Wrapper serializable para almacenar en Redis.</summary>
    private sealed class IndicatorStatesWrapper
    {
        public Dictionary<string, string> States { get; init; } = [];
        public DateTimeOffset SavedAtUtc { get; init; }
    }
}
