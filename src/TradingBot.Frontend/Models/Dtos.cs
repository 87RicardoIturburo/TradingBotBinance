namespace TradingBot.Frontend.Models;

// ── Symbols ───────────────────────────────────────────────────────────────

public sealed record SymbolInfoDto(
    string Symbol,
    string BaseAsset,
    string QuoteAsset);

// ── Strategies ────────────────────────────────────────────────────────────

public sealed record StrategyDto(
    Guid                    Id,
    string                  Name,
    string?                             Description,
    string                              Symbol,
    string                              Status,
    string                              Mode,
    RiskConfigDto                       RiskConfig,
    List<IndicatorDto>                  Indicators,
    List<RuleDto>                       Rules,
    List<SavedParameterRangeDto>?       SavedOptimizationRanges,
    DateTimeOffset                      CreatedAt,
    DateTimeOffset                      UpdatedAt,
    DateTimeOffset?                     LastActivatedAt);

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
    int     MaxOpenPositions);

public sealed record IndicatorDto(
    string                         Type,
    Dictionary<string, decimal>    Parameters);

public sealed record RuleDto(
    Guid                   Id,
    string                 Name,
    string                 Type,
    bool                   IsEnabled,
    string                 Operator,
    List<RuleConditionDto> Conditions,
    string                 ActionType,
    decimal                AmountUsdt);

public sealed record RuleConditionDto(
    string  Indicator,
    string  Comparator,
    decimal Value);

public sealed record OrderDto(
    Guid            Id,
    Guid            StrategyId,
    string          Symbol,
    string          Side,
    string          Type,
    decimal         Quantity,
    decimal?        LimitPrice,
    decimal?        StopPrice,
    decimal?        FilledQuantity,
    decimal?        ExecutedPrice,
    string          Status,
    string          Mode,
    string?         BinanceOrderId,
    DateTimeOffset  CreatedAt,
    DateTimeOffset? FilledAt);

public sealed record SystemStatusDto(
    bool                                      IsRunning,
    bool                                      IsConnected,
    Dictionary<Guid, StrategyEngineStatusDto>? Strategies);

public sealed record StrategyEngineStatusDto(
    Guid           StrategyId,
    string         StrategyName,
    string         Symbol,
    bool           IsProcessing,
    DateTimeOffset LastTickAt,
    int            TicksProcessed,
    int            SignalsGenerated,
    int            OrdersPlaced);

public sealed record MarketTickDto(
    string         Symbol,
    decimal        BidPrice,
    decimal        AskPrice,
    decimal        LastPrice,
    decimal        Volume,
    DateTimeOffset Timestamp);

public sealed record CreateStrategyRequest(
    string  Name,
    string  Symbol,
    string  Mode,
    decimal MaxOrderAmountUsdt,
    decimal MaxDailyLossUsdt,
    decimal StopLossPercent,
    decimal TakeProfitPercent,
    int     MaxOpenPositions,
    string? Description = null);

public sealed record UpdateStrategyRequest(
    string  Name,
    string? Symbol,
    string? Mode,
    decimal MaxOrderAmountUsdt,
    decimal MaxDailyLossUsdt,
    decimal StopLossPercent,
    decimal TakeProfitPercent,
    int     MaxOpenPositions,
    string? Description = null);

public sealed record AddIndicatorRequest(
    string                      Type,
    Dictionary<string, decimal> Parameters);

public sealed record AddRuleRequest(
    string                     Name,
    string                     RuleType,
    string                     Operator,
    List<AddRuleConditionRequest> Conditions,
    string                     ActionType,
    decimal                    AmountUsdt);

public sealed record AddRuleConditionRequest(
    string  Indicator,
    string  Comparator,
    decimal Value);

public sealed record UpdateRuleRequest(
    string                        Name,
    string                        Operator,
    List<AddRuleConditionRequest> Conditions,
    string                        ActionType,
    decimal                       AmountUsdt);

// ── Templates ─────────────────────────────────────────────────────────────

public sealed record StrategyTemplateDto(
    string                             Id,
    string                             Name,
    string                             Description,
    string                             Symbol,
    List<TemplateIndicatorDto>         Indicators,
    List<TemplateRuleDto>              Rules,
    TemplateRiskConfigDto              RiskConfig);

public sealed record TemplateIndicatorDto(
    string                      Type,
    Dictionary<string, decimal> Parameters);

public sealed record TemplateRuleDto(
    string                          Name,
    string                          RuleType,
    string                          Operator,
    List<TemplateConditionDto>      Conditions,
    string                          ActionType,
    decimal                         AmountUsdt);

public sealed record TemplateConditionDto(
    string  Indicator,
    string  Comparator,
    decimal Value);

public sealed record TemplateRiskConfigDto(
    decimal MaxOrderAmountUsdt,
    decimal MaxDailyLossUsdt,
    decimal StopLossPercent,
    decimal TakeProfitPercent,
    int     MaxOpenPositions);

// ── Positions & P&L ───────────────────────────────────────────────────────

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
    DateTimeOffset? ClosedAt);

public sealed record PnLSummaryDto(
    Guid    StrategyId,
    string  StrategyName,
    string  Symbol,
    int     OpenPositions,
    decimal UnrealizedPnL,
    decimal DailyRealizedPnL,
    decimal TotalRealizedPnL);

// ── Backtest ──────────────────────────────────────────────────────────────

public sealed record RunBacktestRequest(
    Guid           StrategyId,
    DateTimeOffset From,
    DateTimeOffset To);

public sealed record BacktestResultDto(
    string                   StrategyName,
    string                   Symbol,
    DateTimeOffset           From,
    DateTimeOffset           To,
    int                      TotalKlines,
    int                      TotalTrades,
    int                      WinningTrades,
    int                      LosingTrades,
    decimal                  WinRate,
    decimal                  TotalPnL,
    decimal                  TotalInvested,
    decimal                  ReturnOnInvestment,
    decimal                  MaxDrawdownPercent,
    decimal                  AveragePnLPerTrade,
    decimal                  BestTrade,
    decimal                  WorstTrade,
    List<BacktestTradeDto>   Trades,
    List<EquityPointDto>     EquityCurve);

public sealed record BacktestTradeDto(
    string         Side,
    decimal        EntryPrice,
    decimal        ExitPrice,
    decimal        Quantity,
    decimal        PnL,
    DateTimeOffset EntryTime,
    DateTimeOffset ExitTime,
    string         ExitReason);

public sealed record EquityPointDto(
    DateTimeOffset Timestamp,
    decimal        Equity);

// ── Optimization ──────────────────────────────────────────────────────────

public sealed record RunOptimizationRequest(
    Guid                    StrategyId,
    DateTimeOffset          From,
    DateTimeOffset          To,
    List<ParameterRangeDto> ParameterRanges);

public sealed record ParameterRangeDto(
    string  Name,
    decimal Min,
    decimal Max,
    decimal Step);

public sealed record OptimizationResultDto(
    string                             StrategyName,
    string                             Symbol,
    DateTimeOffset                     From,
    DateTimeOffset                     To,
    int                                TotalCombinations,
    int                                CompletedCombinations,
    double                             DurationSeconds,
    List<OptimizationRunSummaryDto>    Results);

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
    decimal                     AveragePnLPerTrade);
