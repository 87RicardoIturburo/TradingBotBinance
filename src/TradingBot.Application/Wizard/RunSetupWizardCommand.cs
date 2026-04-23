using MediatR;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TradingBot.Application.Backtesting;
using TradingBot.Application.RiskManagement;
using TradingBot.Core.Common;
using TradingBot.Core.Enums;
using TradingBot.Core.Interfaces.Services;

namespace TradingBot.Application.Wizard;

public sealed record SetupWizardRequest(
    decimal CapitalUsdt,
    string RiskProfile,
    string MonitoringFrequency,
    string TradingModeValue);

public sealed record SetupWizardResult(
    decimal ConfiguredCapital,
    decimal MaxLossUsdt,
    string RiskProfile,
    string TradingMode,
    bool AutoPilotEnabled,
    IReadOnlyList<SetupWizardStrategyCreated> Strategies);

public sealed record SetupWizardStrategyCreated(
    Guid StrategyId,
    string Name,
    string Symbol,
    string TemplateName);

public sealed record RunSetupWizardCommand(
    decimal CapitalUsdt,
    string RiskProfile,
    string MonitoringFrequency,
    string TradingModeValue) : IRequest<Result<SetupWizardResult, DomainError>>;

internal sealed class RunSetupWizardCommandHandler(
    IMarketScanner scanner,
    ISender mediator,
    IStrategyConfigService configService,
    IOptions<RiskBudgetConfig> budgetConfig,
    ILogger<RunSetupWizardCommandHandler> logger)
    : IRequestHandler<RunSetupWizardCommand, Result<SetupWizardResult, DomainError>>
{
    public async Task<Result<SetupWizardResult, DomainError>> Handle(
        RunSetupWizardCommand request,
        CancellationToken cancellationToken)
    {
        var maxLossPercent = request.RiskProfile.ToUpperInvariant() switch
        {
            "CONSERVADOR" or "CONSERVATIVE" => 5m,
            "MODERADO" or "MODERATE"        => 10m,
            "AGRESIVO" or "AGGRESSIVE"      => 20m,
            _ => 10m
        };
        var maxLossUsdt = request.CapitalUsdt * maxLossPercent / 100m;

        var mode = request.TradingModeValue.ToUpperInvariant() switch
        {
            "PAPER" or "PAPERTRADING" => TradingMode.PaperTrading,
            "DEMO" or "TESTNET"       => TradingMode.Testnet,
            "LIVE" or "PRODUCCION"    => TradingMode.Live,
            _                         => TradingMode.PaperTrading
        };

        logger.LogInformation(
            "Setup Wizard: capital={Capital}, perfil={Profile}, modo={Mode}",
            request.CapitalUsdt, request.RiskProfile, mode);

        var scanResult = await scanner.ScanAsync(3, cancellationToken);
        if (scanResult.IsFailure)
            return Result<SetupWizardResult, DomainError>.Failure(scanResult.Error);

        const decimal minAtrPercent = 0.5m;

        var topSymbols = scanResult.Value
            .Where(s => s.TrafficLight == "🟢" && s.AtrPercent >= minAtrPercent)
            .Take(3)
            .ToList();

        if (topSymbols.Count < 2)
        {
            var yellows = scanResult.Value
                .Where(s => s.TrafficLight == "🟡" && s.Score >= 55 && s.AtrPercent >= minAtrPercent)
                .OrderByDescending(s => s.Score)
                .Take(3 - topSymbols.Count);
            topSymbols.AddRange(yellows);
        }

        if (topSymbols.Count == 0)
            return Result<SetupWizardResult, DomainError>.Failure(
                DomainError.Validation("No se encontraron símbolos operables en el escaneo."));

        var createdStrategies = new List<SetupWizardStrategyCreated>();
        var capitalPerSymbol = request.CapitalUsdt / topSymbols.Count;

        foreach (var symbolScore in topSymbols)
        {
            var rankingResult = await mediator.Send(
                new RunTemplateRankingCommand(
                    symbolScore.Symbol, FromDays: 60, InitialBalanceUsdt: request.CapitalUsdt),
                cancellationToken);

            if (rankingResult.IsFailure || rankingResult.Value.Rankings.Count == 0)
                continue;

            var bestTemplate = rankingResult.Value.Rankings[0];

            if (bestTemplate.TotalPnL <= 0
                || bestTemplate.SharpeRatio < 0.3m
                || bestTemplate.TotalTrades < 2
                || bestTemplate.ProfitFactor < 1.1m)
            {
                logger.LogWarning(
                    "Template '{Name}' descartado para {Symbol}: Sharpe={Sharpe:F2}, PnL={PnL:F2}, Trades={Trades}, PF={PF:F2}, WR={WR:F1}%",
                    bestTemplate.TemplateName, symbolScore.Symbol,
                    bestTemplate.SharpeRatio, bestTemplate.TotalPnL, bestTemplate.TotalTrades,
                    bestTemplate.ProfitFactor, bestTemplate.WinRate);
                continue;
            }

            var template = StrategyTemplateStore.All
                .FirstOrDefault(t => t.Id == bestTemplate.TemplateId);

            if (template is null) continue;

            var profile = rankingResult.Value.Profile;

            var created = await CreateStrategyFromTemplateAsync(
                template, symbolScore.Symbol, mode, capitalPerSymbol, profile, cancellationToken);

            if (created is not null)
                createdStrategies.Add(created);
        }

        if (createdStrategies.Count == 0)
            return Result<SetupWizardResult, DomainError>.Failure(
                DomainError.InvalidOperation("No se pudo crear ninguna estrategia."));

        var autoPilotEnabled = request.MonitoringFrequency.Equals(
            "NoQuieroPensar", StringComparison.OrdinalIgnoreCase);

        return Result<SetupWizardResult, DomainError>.Success(new SetupWizardResult(
            request.CapitalUsdt,
            maxLossUsdt,
            request.RiskProfile,
            mode.ToString(),
            autoPilotEnabled,
            createdStrategies));
    }

    private async Task<SetupWizardStrategyCreated?> CreateStrategyFromTemplateAsync(
        StrategyTemplateDto template,
        string symbol,
        TradingMode mode,
        decimal capitalForSymbol,
        Backtesting.SymbolProfile profile,
        CancellationToken ct)
    {
        var symbolResult = Core.ValueObjects.Symbol.Create(symbol);
        if (symbolResult.IsFailure) return null;

        var maxOrder = Math.Min(template.RiskConfig.MaxOrderAmountUsdt, capitalForSymbol * 0.3m);

        var timeframe = Enum.TryParse<CandleInterval>(template.RiskConfig.Timeframe, true, out var tf)
            ? tf : CandleInterval.OneHour;
        CandleInterval? confirmationTf = template.RiskConfig.ConfirmationTimeframe is not null
            && Enum.TryParse<CandleInterval>(template.RiskConfig.ConfirmationTimeframe, true, out var ctf)
            ? ctf : null;

        var riskResult = Core.ValueObjects.RiskConfig.Create(
            maxOrder,
            template.RiskConfig.MaxDailyLossUsdt,
            template.RiskConfig.StopLossPercent,
            template.RiskConfig.TakeProfitPercent,
            template.RiskConfig.MaxOpenPositions,
            template.RiskConfig.UseAtrSizing,
            template.RiskConfig.RiskPercentPerTrade,
            template.RiskConfig.AtrMultiplier,
            useTrailingStop: template.RiskConfig.UseTrailingStop,
            trailingStopPercent: template.RiskConfig.TrailingStopPercent,
            signalCooldownPercent: template.RiskConfig.SignalCooldownPercent,
            minConfirmationPercent: template.RiskConfig.MinConfirmationPercent,
            highVolatilityBandWidthPercent: profile.AdjustedHighVolatilityBandWidthPercent,
            highVolatilityAtrPercent: profile.AdjustedHighVolatilityAtrPercent,
            maxSpreadPercent: profile.AdjustedMaxSpreadPercent,
            takeProfit1Percent: template.RiskConfig.TakeProfit1Percent,
            takeProfit1ClosePercent: template.RiskConfig.TakeProfit1ClosePercent,
            takeProfit2Percent: template.RiskConfig.TakeProfit2Percent,
            takeProfit2ClosePercent: template.RiskConfig.TakeProfit2ClosePercent,
            exitOnRegimeChange: template.RiskConfig.ExitOnRegimeChange,
            maxPositionDurationCandles: template.RiskConfig.MaxPositionDurationCandles,
            takeProfit1AtrMultiplier: template.RiskConfig.TakeProfit1AtrMultiplier,
            takeProfit2AtrMultiplier: template.RiskConfig.TakeProfit2AtrMultiplier);

        if (riskResult.IsFailure) return null;

        var strategyName = $"Wizard — {template.Name} — {symbol}";
        var strategyResult = Core.Entities.TradingStrategy.Create(
            strategyName, symbolResult.Value, mode, riskResult.Value,
            $"Creada por Setup Wizard desde {template.Name}",
            timeframe, confirmationTf);

        if (strategyResult.IsFailure) return null;

        var strategy = strategyResult.Value;

        foreach (var ind in template.Indicators)
        {
            if (Enum.TryParse<IndicatorType>(ind.Type, true, out var indType))
            {
                var indConfig = Core.ValueObjects.IndicatorConfig.Create(indType, ind.Parameters);
                if (indConfig.IsSuccess)
                    strategy.AddIndicator(indConfig.Value);
            }
        }

        foreach (var rule in template.Rules)
        {
            if (Enum.TryParse<RuleType>(rule.RuleType, true, out var ruleType)
                && Enum.TryParse<ConditionOperator>(rule.Operator, true, out var condOp)
                && Enum.TryParse<ActionType>(rule.ActionType, true, out var actionType))
            {
                var conditions = rule.Conditions
                    .Where(c => Enum.TryParse<IndicatorType>(c.Indicator, true, out _)
                             && Enum.TryParse<Comparator>(c.Comparator, true, out _))
                    .Select(c => new Core.ValueObjects.LeafCondition(
                        Enum.Parse<IndicatorType>(c.Indicator, true),
                        Enum.Parse<Comparator>(c.Comparator, true),
                        c.Value))
                    .ToList();

                if (conditions.Count > 0)
                {
                    var ruleCondition = new Core.ValueObjects.RuleCondition(condOp, conditions);
                    var ruleAction = new Core.ValueObjects.RuleAction(actionType, rule.AmountUsdt);
                    var tradingRule = Core.Entities.TradingRule.Create(
                        strategy.Id, rule.Name, ruleType, ruleCondition, ruleAction);
                    if (tradingRule.IsSuccess)
                        strategy.AddRule(tradingRule.Value);
                }
            }
        }

        var createResult = await configService.CreateAsync(strategy, ct);
        if (createResult.IsFailure) return null;

        var activateResult = await configService.ActivateAsync(createResult.Value.Id, ct);
        if (activateResult.IsFailure) return null;

        return new SetupWizardStrategyCreated(
            createResult.Value.Id, strategyName, symbol, template.Name);
    }
}
