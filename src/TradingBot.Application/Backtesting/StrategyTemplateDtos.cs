namespace TradingBot.Application.Backtesting;

/// <summary>
/// Records para definir templates de estrategia en la capa Application.
/// Las plantillas son datos puros sin dependencias de infraestructura.
/// </summary>
public sealed record StrategyTemplateDto(
    string                             Id,
    string                             Name,
    string                             Description,
    string                             Symbol,
    List<TemplateIndicatorDto>         Indicators,
    List<TemplateRuleDto>              Rules,
    StrategyTemplateRiskConfigDto      RiskConfig);

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

public sealed record StrategyTemplateRiskConfigDto(
    decimal MaxOrderAmountUsdt,
    decimal MaxDailyLossUsdt,
    decimal StopLossPercent,
    decimal TakeProfitPercent,
    int     MaxOpenPositions,
    bool    UseAtrSizing = false,
    decimal RiskPercentPerTrade = 1m,
    decimal AtrMultiplier = 2m,
    string  Timeframe = "OneHour",
    string? ConfirmationTimeframe = null);
