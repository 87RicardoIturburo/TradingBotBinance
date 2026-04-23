using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using TradingBot.Core.Entities;
using TradingBot.Core.Enums;
using TradingBot.Core.Interfaces.Trading;

namespace TradingBot.Application.Strategies;

/// <summary>
/// Resuelve la estrategia correcta según el régimen de mercado detectado.
/// Cachea instancias por strategyId (NO incluye régimen en la clave).
/// Al cambiar régimen solo se activa otra instancia — los indicadores NO se reconstruyen.
/// </summary>
internal sealed class StrategyResolver
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ConcurrentDictionary<Guid, StrategyInstanceSet> _cache = new();

    public StrategyResolver(IServiceProvider serviceProvider)
    {
        _serviceProvider = serviceProvider;
    }

    public ITradingStrategy? Resolve(Guid strategyId, MarketRegime regime, TradingStrategy config)
    {
        var set = _cache.GetOrAdd(strategyId, _ => CreateInstanceSet());

        var strategy = regime switch
        {
            MarketRegime.Trending    => set.Trending,
            MarketRegime.Ranging     => set.Ranging,
            MarketRegime.Bearish     => set.Bearish,
            MarketRegime.Unknown     => set.Default,
            MarketRegime.Indefinite  => null,
            MarketRegime.HighVolatility => null,
            _ => set.Default
        };

        return strategy;
    }

    public async Task InitializeSetAsync(Guid strategyId, TradingStrategy config, CancellationToken ct = default)
    {
        var set = _cache.GetOrAdd(strategyId, _ => CreateInstanceSet());
        await set.Default.InitializeAsync(config, ct);
        await set.Trending.InitializeAsync(config, ct);
        await set.Ranging.InitializeAsync(config, ct);
        await set.Bearish.InitializeAsync(config, ct);
    }

    public void Remove(Guid strategyId) => _cache.TryRemove(strategyId, out _);

    public IReadOnlyList<ITradingStrategy> GetAllStrategies(Guid strategyId)
    {
        if (!_cache.TryGetValue(strategyId, out var set))
            return [];
        return [set.Default, set.Trending, set.Ranging, set.Bearish];
    }

    public async Task ReloadAllAsync(Guid strategyId, TradingStrategy config, CancellationToken ct = default)
    {
        if (!_cache.TryGetValue(strategyId, out var set)) return;
        await set.Default.ReloadConfigAsync(config, ct);
        await set.Trending.ReloadConfigAsync(config, ct);
        await set.Ranging.ReloadConfigAsync(config, ct);
        await set.Bearish.ReloadConfigAsync(config, ct);
    }

    private StrategyInstanceSet CreateInstanceSet()
    {
        return new StrategyInstanceSet(
            _serviceProvider.GetRequiredService<DefaultTradingStrategy>(),
            _serviceProvider.GetRequiredService<TrendingTradingStrategy>(),
            _serviceProvider.GetRequiredService<RangingTradingStrategy>(),
            _serviceProvider.GetRequiredService<BearishTradingStrategy>());
    }

    private sealed record StrategyInstanceSet(
        DefaultTradingStrategy Default,
        TrendingTradingStrategy Trending,
        RangingTradingStrategy Ranging,
        BearishTradingStrategy Bearish);
}
