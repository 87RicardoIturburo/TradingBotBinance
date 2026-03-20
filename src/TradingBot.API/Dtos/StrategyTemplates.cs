using TradingBot.Application.Backtesting;

namespace TradingBot.API.Dtos;

/// <summary>
/// Delega al <see cref="StrategyTemplateStore"/> de la capa Application.
/// Convierte los DTOs de Application a los DTOs de la API/Frontend.
/// </summary>
public static class StrategyTemplates
{
    public static IReadOnlyList<StrategyTemplateDto> All { get; } =
        StrategyTemplateStore.All
            .Select(t => new StrategyTemplateDto(
                t.Id, t.Name, t.Description, t.Symbol,
                t.Indicators.Select(i => new TemplateIndicatorDto(i.Type, i.Parameters)).ToList(),
                t.Rules.Select(r => new TemplateRuleDto(
                    r.Name, r.RuleType, r.Operator,
                    r.Conditions.Select(c => new TemplateConditionDto(c.Indicator, c.Comparator, c.Value)).ToList(),
                    r.ActionType, r.AmountUsdt)).ToList(),
                new TemplateRiskConfigDto(
                    t.RiskConfig.MaxOrderAmountUsdt, t.RiskConfig.MaxDailyLossUsdt,
                    t.RiskConfig.StopLossPercent, t.RiskConfig.TakeProfitPercent,
                    t.RiskConfig.MaxOpenPositions, t.RiskConfig.UseAtrSizing,
                    t.RiskConfig.RiskPercentPerTrade, t.RiskConfig.AtrMultiplier,
                    t.RiskConfig.Timeframe, t.RiskConfig.ConfirmationTimeframe)))
            .ToList();
}

// ── Template DTOs (API layer — consumed by Frontend) ──────────────────────

public sealed record StrategyTemplateDto(
    string                       Id,
    string                       Name,
    string                       Description,
    string                       Symbol,
    List<TemplateIndicatorDto>   Indicators,
    List<TemplateRuleDto>        Rules,
    TemplateRiskConfigDto        RiskConfig);

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
    int     MaxOpenPositions,
    bool    UseAtrSizing = false,
    decimal RiskPercentPerTrade = 1m,
    decimal AtrMultiplier = 2m,
    string  Timeframe = "OneHour",
    string? ConfirmationTimeframe = null);
