namespace TradingBot.Frontend.Models;

public sealed record StrategyDto(
    Guid                    Id,
    string                  Name,
    string?                 Description,
    string                  Symbol,
    string                  Status,
    string                  Mode,
    RiskConfigDto           RiskConfig,
    List<IndicatorDto>      Indicators,
    List<RuleDto>           Rules,
    DateTimeOffset          CreatedAt,
    DateTimeOffset          UpdatedAt,
    DateTimeOffset?         LastActivatedAt);

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
    Guid   Id,
    string Name,
    string Type,
    bool   IsEnabled);

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
