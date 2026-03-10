namespace TradingBot.Core.ValueObjects;

/// <summary>
/// Rango de parámetro guardado para reutilizar en optimizaciones futuras.
/// </summary>
public sealed record SavedParameterRange(
    string  Name,
    decimal Min,
    decimal Max,
    decimal Step);
