using TradingBot.Core.Common;

namespace TradingBot.Core.Interfaces.Services;

/// <summary>
/// Escanea el mercado y calcula un Tradability Score para cada símbolo.
/// Permite al usuario novato saber qué pares vale la pena operar.
/// </summary>
public interface IMarketScanner
{
    /// <summary>Ejecuta un escaneo completo del mercado.</summary>
    Task<Result<IReadOnlyList<SymbolScore>, DomainError>> ScanAsync(
        int topCount = 50,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Resultado del escaneo para un símbolo individual.
/// Score 0–100 con semáforo: 🟢 ≥ 70, 🟡 40–69, 🔴 &lt; 40.
/// </summary>
public sealed record SymbolScore(
    string Symbol,
    decimal Score,
    string TrafficLight,
    decimal Volume24hUsdt,
    decimal SpreadPercent,
    decimal AtrPercent,
    string Regime,
    decimal? AdxValue,
    decimal PriceChangePercent24h,
    DateTimeOffset ScannedAt);
