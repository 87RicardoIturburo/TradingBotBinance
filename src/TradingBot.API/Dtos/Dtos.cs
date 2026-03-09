using TradingBot.Core.Entities;
using TradingBot.Core.Enums;
using TradingBot.Core.Interfaces.Services;
using TradingBot.Core.ValueObjects;

namespace TradingBot.API.Dtos;

/// <summary>Respuesta de estrategia para el frontend.</summary>
public sealed record StrategyDto(
    Guid                          Id,
    string                        Name,
    string?                       Description,
    string                        Symbol,
    StrategyStatus                Status,
    TradingMode                   Mode,
    RiskConfigDto                 RiskConfig,
    IReadOnlyList<IndicatorDto>   Indicators,
    IReadOnlyList<RuleDto>        Rules,
    DateTimeOffset                CreatedAt,
    DateTimeOffset                UpdatedAt,
    DateTimeOffset?               LastActivatedAt)
{
    public static StrategyDto FromDomain(TradingStrategy s) => new(
        s.Id, s.Name, s.Description, s.Symbol.Value,
        s.Status, s.Mode,
        RiskConfigDto.FromDomain(s.RiskConfig),
        s.Indicators.Select(IndicatorDto.FromDomain).ToList(),
        s.Rules.Select(RuleDto.FromDomain).ToList(),
        s.CreatedAt, s.UpdatedAt, s.LastActivatedAt);
}

public sealed record RiskConfigDto(
    decimal MaxOrderAmountUsdt,
    decimal MaxDailyLossUsdt,
    decimal StopLossPercent,
    decimal TakeProfitPercent,
    int     MaxOpenPositions)
{
    public static RiskConfigDto FromDomain(RiskConfig r) => new(
        r.MaxOrderAmountUsdt, r.MaxDailyLossUsdt,
        r.StopLossPercent.Value, r.TakeProfitPercent.Value,
        r.MaxOpenPositions);
}

public sealed record IndicatorDto(
    IndicatorType                   Type,
    IReadOnlyDictionary<string, decimal> Parameters)
{
    public static IndicatorDto FromDomain(IndicatorConfig c) => new(c.Type, c.Parameters);
}

public sealed record RuleDto(
    Guid     Id,
    string   Name,
    RuleType Type,
    bool     IsEnabled)
{
    public static RuleDto FromDomain(TradingRule r) => new(r.Id, r.Name, r.Type, r.IsEnabled);
}

/// <summary>Respuesta de orden para el frontend.</summary>
public sealed record OrderDto(
    Guid            Id,
    Guid            StrategyId,
    string          Symbol,
    OrderSide       Side,
    OrderType       Type,
    decimal         Quantity,
    decimal?        LimitPrice,
    decimal?        StopPrice,
    decimal?        FilledQuantity,
    decimal?        ExecutedPrice,
    OrderStatus     Status,
    TradingMode     Mode,
    string?         BinanceOrderId,
    DateTimeOffset  CreatedAt,
    DateTimeOffset? FilledAt)
{
    public static OrderDto FromDomain(Order o) => new(
        o.Id, o.StrategyId, o.Symbol.Value,
        o.Side, o.Type, o.Quantity.Value,
        o.LimitPrice?.Value, o.StopPrice?.Value,
        o.FilledQuantity?.Value, o.ExecutedPrice?.Value,
        o.Status, o.Mode, o.BinanceOrderId,
        o.CreatedAt, o.FilledAt);
}

/// <summary>Estado del sistema devuelto por /api/system/status.</summary>
public sealed record SystemStatusDto(
    bool                                            IsRunning,
    bool                                            IsConnected,
    IReadOnlyDictionary<Guid, StrategyEngineStatus> Strategies);
