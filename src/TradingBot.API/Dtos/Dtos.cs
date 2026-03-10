using TradingBot.Core.Entities;
using TradingBot.Core.Enums;
using TradingBot.Core.Interfaces.Services;
using TradingBot.Core.ValueObjects;
using TradingBot.Application.Queries.Positions;
using TradingBot.Application.Backtesting;

namespace TradingBot.API.Dtos;

/// <summary>Par de trading disponible en Binance.</summary>
public sealed record SymbolInfoDto(
    string Symbol,
    string BaseAsset,
    string QuoteAsset);

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
    IReadOnlyList<SavedParameterRangeDto> SavedOptimizationRanges,
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
        s.SavedOptimizationRanges.Select(r => new SavedParameterRangeDto(r.Name, r.Min, r.Max, r.Step)).ToList(),
        s.CreatedAt, s.UpdatedAt, s.LastActivatedAt);
}

public sealed record SavedParameterRangeDto(
    string  Name,
    decimal Min,
    decimal Max,
    decimal Step);

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
    Guid                       Id,
    string                     Name,
    RuleType                   Type,
    bool                       IsEnabled,
    ConditionOperator          Operator,
    IReadOnlyList<ConditionDto> Conditions,
    ActionType                 ActionType,
    decimal                    AmountUsdt)
{
    public static RuleDto FromDomain(TradingRule r) => new(
        r.Id, r.Name, r.Type, r.IsEnabled,
        r.Condition.Operator,
        r.Condition.Conditions.Select(c => new ConditionDto(c.Indicator, c.Comparator, c.Value)).ToList(),
        r.Action.Type, r.Action.AmountUsdt);
}

public sealed record ConditionDto(
    IndicatorType Indicator,
    Comparator    Comparator,
    decimal       Value);

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
    bool                                                IsRunning,
    bool                                                IsConnected,
    IReadOnlyDictionary<Guid, StrategyEngineStatusDto>  Strategies);

/// <summary>Estado de una estrategia en el motor, serializado para el frontend.</summary>
public sealed record StrategyEngineStatusDto(
    Guid           StrategyId,
    string         StrategyName,
    string         Symbol,
    bool           IsProcessing,
    DateTimeOffset LastTickAt,
    int            TicksProcessed,
    int            SignalsGenerated,
    int            OrdersPlaced)
{
    public static StrategyEngineStatusDto FromDomain(StrategyEngineStatus s) => new(
        s.StrategyId, s.StrategyName, s.Symbol.Value,
        s.IsProcessing, s.LastTickAt,
        s.TicksProcessed, s.SignalsGenerated, s.OrdersPlaced);
}

/// <summary>Posición abierta o cerrada para el frontend.</summary>
public sealed record PositionDto(
    Guid            Id,
    Guid            StrategyId,
    string          Symbol,
    string          Side,
    decimal         EntryPrice,
    decimal         CurrentPrice,
    decimal         Quantity,
    bool            IsOpen,
    decimal         UnrealizedPnL,
    decimal         UnrealizedPnLPercent,
    decimal?        RealizedPnL,
    DateTimeOffset  OpenedAt,
    DateTimeOffset? ClosedAt)
{
    public static PositionDto FromDomain(Position p) => new(
        p.Id, p.StrategyId, p.Symbol.Value,
        p.Side.ToString(), p.EntryPrice.Value, p.CurrentPrice.Value,
        p.Quantity.Value, p.IsOpen,
        p.UnrealizedPnL, p.UnrealizedPnLPercent,
        p.RealizedPnL, p.OpenedAt, p.ClosedAt);
}

/// <summary>Resumen de P&amp;L por estrategia.</summary>
public sealed record PnLSummaryDto(
    Guid    StrategyId,
    string  StrategyName,
    string  Symbol,
    int     OpenPositions,
    decimal UnrealizedPnL,
    decimal DailyRealizedPnL,
    decimal TotalRealizedPnL)
{
    public static PnLSummaryDto FromDomain(PnLSummaryItem item) => new(
        item.StrategyId, item.StrategyName, item.Symbol,
        item.OpenPositions, item.UnrealizedPnL,
        item.DailyRealizedPnL, item.TotalRealizedPnL);
}

// ── Backtest ──────────────────────────────────────────────────────────────

/// <summary>Request para ejecutar un backtest.</summary>
public sealed record RunBacktestRequest(
    Guid           StrategyId,
    DateTimeOffset From,
    DateTimeOffset To);

/// <summary>Resultado completo de un backtest para el frontend.</summary>
public sealed record BacktestResultDto(
    string                          StrategyName,
    string                          Symbol,
    DateTimeOffset                  From,
    DateTimeOffset                  To,
    int                             TotalKlines,
    int                             TotalTrades,
    int                             WinningTrades,
    int                             LosingTrades,
    decimal                         WinRate,
    decimal                         TotalPnL,
    decimal                         TotalInvested,
    decimal                         ReturnOnInvestment,
    decimal                         MaxDrawdownPercent,
    decimal                         AveragePnLPerTrade,
    decimal                         BestTrade,
    decimal                         WorstTrade,
    List<BacktestTradeDto>          Trades,
    List<EquityPointDto>            EquityCurve)
{
    public static BacktestResultDto FromDomain(BacktestResult r) => new(
        r.StrategyName, r.Symbol, r.From, r.To,
        r.TotalKlines, r.TotalTrades, r.WinningTrades, r.LosingTrades,
        r.WinRate, r.TotalPnL, r.TotalInvested, r.ReturnOnInvestment,
        r.MaxDrawdownPercent, r.AveragePnLPerTrade,
        r.BestTrade, r.WorstTrade,
        r.Trades.Select(BacktestTradeDto.FromDomain).ToList(),
        r.EquityCurve.Select(EquityPointDto.FromDomain).ToList());
}

public sealed record BacktestTradeDto(
    string         Side,
    decimal        EntryPrice,
    decimal        ExitPrice,
    decimal        Quantity,
    decimal        PnL,
    DateTimeOffset EntryTime,
    DateTimeOffset ExitTime,
    string         ExitReason)
{
    public static BacktestTradeDto FromDomain(BacktestTrade t) => new(
        t.Side.ToString(), t.EntryPrice, t.ExitPrice,
        t.Quantity, t.PnL, t.EntryTime, t.ExitTime, t.ExitReason);
}

public sealed record EquityPointDto(
    DateTimeOffset Timestamp,
    decimal        Equity)
{
    public static EquityPointDto FromDomain(EquityPoint p) => new(p.Timestamp, p.Equity);
}

// ── Optimization ──────────────────────────────────────────────────────────

/// <summary>Request para ejecutar una optimización de parámetros.</summary>
public sealed record RunOptimizationRequest(
    Guid                       StrategyId,
    DateTimeOffset             From,
    DateTimeOffset             To,
    List<ParameterRangeDto>    ParameterRanges);

public sealed record ParameterRangeDto(
    string  Name,
    decimal Min,
    decimal Max,
    decimal Step);

/// <summary>Resultado completo de una optimización para el frontend.</summary>
public sealed record OptimizationResultDto(
    string                              StrategyName,
    string                              Symbol,
    DateTimeOffset                      From,
    DateTimeOffset                      To,
    int                                 TotalCombinations,
    int                                 CompletedCombinations,
    double                              DurationSeconds,
    List<OptimizationRunSummaryDto>     Results)
{
    public static OptimizationResultDto FromDomain(OptimizationResult r) => new(
        r.StrategyName, r.Symbol, r.From, r.To,
        r.TotalCombinations, r.CompletedCombinations,
        r.Duration.TotalSeconds,
        r.Results.Select(OptimizationRunSummaryDto.FromDomain).ToList());
}

public sealed record OptimizationRunSummaryDto(
    int                         Rank,
    Dictionary<string, decimal> Parameters,
    int                         TotalTrades,
    int                         WinningTrades,
    decimal                     WinRate,
    decimal                     TotalPnL,
    decimal                     TotalInvested,
    decimal                     ReturnOnInvestment,
    decimal                     MaxDrawdownPercent,
    decimal                     AveragePnLPerTrade)
{
    public static OptimizationRunSummaryDto FromDomain(OptimizationRunSummary r) => new(
        r.Rank, r.Parameters, r.TotalTrades, r.WinningTrades,
        r.WinRate, r.TotalPnL, r.TotalInvested, r.ReturnOnInvestment,
        r.MaxDrawdownPercent, r.AveragePnLPerTrade);
}
