namespace TradingBot.Core.Interfaces.Services;

/// <summary>
/// Consulta y control del pool dinámico de símbolos (AutoPilot v2).
/// El estado <c>Enabled</c> es in-memory; default <c>false</c>.
/// </summary>
public interface ISymbolPool
{
    Task<IReadOnlyList<string>> GetObservedSymbolsAsync(CancellationToken ct = default);
    Task<IReadOnlyList<string>> GetActiveSymbolsAsync(CancellationToken ct = default);
    Task<IReadOnlyList<SymbolTradabilityInfo>> GetTradabilityScoresAsync(CancellationToken ct = default);
    Task<bool> IsEnabledAsync(CancellationToken ct = default);
    Task SetEnabledAsync(bool enabled, CancellationToken ct = default);
    Task ForceRefreshAsync(CancellationToken ct = default);
    Task ExcludeSymbolAsync(string symbol, TimeSpan duration, CancellationToken ct = default);
}

/// <summary>
/// Snapshot de un símbolo dentro del pool con su score y motivo de bloqueo.
/// </summary>
public sealed record SymbolTradabilityInfo(
    string Symbol,
    decimal Score,
    string Regime,
    bool IsActive,
    bool AllowNewEntries,
    string? BlockReason,
    decimal RegimeStability);
