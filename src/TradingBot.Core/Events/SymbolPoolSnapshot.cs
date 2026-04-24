namespace TradingBot.Core.Events;

/// <summary>
/// Snapshot completo del estado del pool de símbolos.
/// Publicado vía SignalR al frontend en cada ciclo de evaluación.
/// </summary>
public sealed record SymbolPoolSnapshot(
    bool Enabled,
    int EvaluatedCount,
    int BlockedByRegime,
    int BlockedByScore,
    int BlockedByCooldown,
    int ActiveCount,
    int ZombiesRemoved,
    IReadOnlyList<SymbolPoolItemSnapshot> Items,
    DateTimeOffset Timestamp);

/// <summary>
/// Snapshot de un símbolo individual dentro del pool.
/// </summary>
public sealed record SymbolPoolItemSnapshot(
    string Symbol,
    decimal Score,
    string Regime,
    bool IsActive,
    bool AllowNewEntries,
    string? BlockReason,
    decimal RegimeStability);
