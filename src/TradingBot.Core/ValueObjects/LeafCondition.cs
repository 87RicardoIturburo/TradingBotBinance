using TradingBot.Core.Enums;

namespace TradingBot.Core.ValueObjects;

/// <summary>
/// Condición atómica: compara el valor de un indicador con un umbral numérico.
/// </summary>
public sealed record LeafCondition(
    IndicatorType Indicator,
    Comparator    Comparator,
    decimal       Value);
