using System.Globalization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TradingBot.Application.Strategies.Indicators;
using TradingBot.Core.Common;
using TradingBot.Core.Enums;
using TradingBot.Core.Interfaces.Services;

namespace TradingBot.Application.Scanner;

/// <summary>
/// Implementación del Market Scanner. Analiza los top símbolos por volumen 24h
/// y calcula un Tradability Score compuesto para cada uno.
/// </summary>
internal sealed class MarketScannerService : IMarketScanner
{
    private readonly IMarketDataService _marketData;
    private readonly ICacheService _cache;
    private readonly MarketScannerConfig _config;
    private readonly ILogger<MarketScannerService> _logger;

    private const string CacheKeyPrefix = "scanner:scores";
    private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(5);

    public MarketScannerService(
        IMarketDataService marketData,
        ICacheService cache,
        IOptions<MarketScannerConfig> config,
        ILogger<MarketScannerService> logger)
    {
        _marketData = marketData;
        _cache = cache;
        _config = config.Value;
        _logger = logger;
    }

    public async Task<Result<IReadOnlyList<SymbolScore>, DomainError>> ScanAsync(
        int topCount = 50,
        CancellationToken cancellationToken = default)
    {
        var cached = await _cache.GetAsync<List<SymbolScore>>(CacheKeyPrefix, cancellationToken);
        if (cached is not null)
            return Result<IReadOnlyList<SymbolScore>, DomainError>.Success(cached);

        var tickersResult = await _marketData.Get24hTickersAsync(cancellationToken);
        if (tickersResult.IsFailure)
            return Result<IReadOnlyList<SymbolScore>, DomainError>.Failure(tickersResult.Error);

        var symbolsResult = await _marketData.GetTradingSymbolsAsync(_config.QuoteAsset, cancellationToken);
        if (symbolsResult.IsFailure)
            return Result<IReadOnlyList<SymbolScore>, DomainError>.Failure(symbolsResult.Error);

        var tradingSymbols = symbolsResult.Value
            .Select(s => s.Symbol)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var candidates = tickersResult.Value
            .Where(t => tradingSymbols.Contains(t.Symbol))
            .Where(t => t.QuoteVolume24h >= _config.MinVolume24hUsdt)
            .OrderByDescending(t => t.QuoteVolume24h)
            .Take(Math.Max(topCount, _config.TopSymbolsCount))
            .ToList();

        var scores = new List<SymbolScore>(candidates.Count);
        var now = DateTimeOffset.UtcNow;

        foreach (var ticker in candidates)
        {
            var score = CalculateScore(ticker);
            scores.Add(score with { ScannedAt = now });
        }

        scores = [.. scores.OrderByDescending(s => s.Score)];

        await _cache.SetAsync(CacheKeyPrefix, scores, CacheTtl, cancellationToken);

        _logger.LogInformation(
            "Market scan completado: {Count} símbolos analizados, top score={TopScore:F1} ({TopSymbol})",
            scores.Count,
            scores.Count > 0 ? scores[0].Score : 0,
            scores.Count > 0 ? scores[0].Symbol : "N/A");

        return Result<IReadOnlyList<SymbolScore>, DomainError>.Success(scores);
    }

    internal SymbolScore CalculateScore(Ticker24h ticker)
    {
        var spreadPercent = ticker.LastPrice > 0
            ? (ticker.AskPrice - ticker.BidPrice) / ticker.LastPrice * 100m
            : 100m;

        var atrPercent = ticker.LastPrice > 0 && ticker.HighPrice24h > 0
            ? (ticker.HighPrice24h - ticker.LowPrice24h) / ticker.LastPrice * 100m
            : 0m;

        var volumeScore = ScoreVolume(ticker.QuoteVolume24h);
        var spreadScore = ScoreSpread(spreadPercent);
        var atrScore = ScoreAtr(atrPercent);
        var (regimeScore, regimeLabel) = ScoreRegime(atrPercent, spreadPercent);
        var adxScore = ScoreAdxProxy(atrPercent, Math.Abs(ticker.PriceChangePercent24h));
        var feeViabilityScore = ScoreFeeViability(atrPercent);

        var totalWeight = _config.VolumeWeight + _config.SpreadWeight
                        + _config.AtrWeight + _config.RegimeWeight + _config.AdxWeight
                        + _config.FeeViabilityWeight;

        var weightedScore = totalWeight > 0
            ? (volumeScore * _config.VolumeWeight
             + spreadScore * _config.SpreadWeight
             + atrScore * _config.AtrWeight
             + regimeScore * _config.RegimeWeight
             + adxScore * _config.AdxWeight
             + feeViabilityScore * _config.FeeViabilityWeight) / totalWeight
            : 0m;

        var trafficLight = weightedScore >= 70 ? "🟢" : weightedScore >= 40 ? "🟡" : "🔴";

        return new SymbolScore(
            ticker.Symbol,
            Math.Round(weightedScore, 1),
            trafficLight,
            ticker.QuoteVolume24h,
            Math.Round(spreadPercent, 4),
            Math.Round(atrPercent, 2),
            regimeLabel,
            null,
            ticker.PriceChangePercent24h,
            default);
    }

    private static decimal ScoreVolume(decimal quoteVolume24h) => quoteVolume24h switch
    {
        >= 50_000_000m => 100m,
        >= 10_000_000m => 60m + (quoteVolume24h - 10_000_000m) / 40_000_000m * 40m,
        >= 1_000_000m  => 20m + (quoteVolume24h - 1_000_000m) / 9_000_000m * 40m,
        _              => Math.Max(0, quoteVolume24h / 1_000_000m * 20m)
    };

    private static decimal ScoreSpread(decimal spreadPercent) => spreadPercent switch
    {
        <= 0.05m  => 100m,
        <= 0.15m  => 50m + (0.15m - spreadPercent) / 0.10m * 50m,
        <= 0.50m  => 10m + (0.50m - spreadPercent) / 0.35m * 40m,
        _         => 0m
    };

    private static decimal ScoreAtr(decimal atrPercent) => atrPercent switch
    {
        >= 1m and <= 4m => 100m,
        > 0.5m and < 1m => 50m + (atrPercent - 0.5m) / 0.5m * 50m,
        > 4m and <= 6m  => 50m + (6m - atrPercent) / 2m * 50m,
        < 0.5m          => atrPercent / 0.5m * 50m,
        _               => 0m
    };

    private static (decimal Score, string Label) ScoreRegime(decimal atrPercent, decimal spreadPercent)
    {
        if (atrPercent > 6m || spreadPercent > 0.5m)
            return (20m, MarketRegime.HighVolatility.ToString());

        if (atrPercent >= 1.5m && atrPercent <= 5m)
            return (100m, MarketRegime.Trending.ToString());

        if (atrPercent < 0.8m)
            return (60m, MarketRegime.Ranging.ToString());

        return (70m, MarketRegime.Unknown.ToString());
    }

    private static decimal ScoreAdxProxy(decimal atrPercent, decimal absPriceChangePercent)
    {
        var directionalStrength = absPriceChangePercent > 0 && atrPercent > 0
            ? Math.Min(absPriceChangePercent / atrPercent * 100m, 100m)
            : 0m;

        return directionalStrength switch
        {
            >= 50m => 100m,
            >= 25m => 50m + (directionalStrength - 25m) / 25m * 50m,
            _      => directionalStrength / 25m * 50m
        };
    }

    /// <summary>
    /// Penaliza symbols donde el ATR% es demasiado bajo respecto al costo de fees round-trip.
    /// Un ratio ATR/fees menor a 3× hace el symbol prácticamente no-tradeable.
    /// </summary>
    private static decimal ScoreFeeViability(decimal atrPercent)
    {
        const decimal roundTripFeePercent = 0.15m;
        var ratio = roundTripFeePercent > 0 ? atrPercent / roundTripFeePercent : 10m;

        return ratio switch
        {
            >= 10m => 100m,
            >= 5m  => 80m,
            >= 3m  => 60m,
            >= 2m  => 30m,
            _      => 0m
        };
    }
}
