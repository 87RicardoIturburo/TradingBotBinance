using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TradingBot.Application.Backtesting;
using TradingBot.Core.Common;
using TradingBot.Core.Enums;
using TradingBot.Core.Interfaces.Services;

namespace TradingBot.Application.AutoPilot;

/// <summary>
/// Implementación del rotador de estrategias. Evalúa el régimen de mercado
/// y decide si rotar la estrategia activa para un symbol dado.
/// Respeta cooldown de rotación y transiciones suaves.
/// </summary>
internal sealed class StrategyRotatorService : IStrategyRotator
{
    private readonly IStrategyConfigService _configService;
    private readonly IStrategyEngine _engine;
    private readonly AutoPilotConfig _config;
    private readonly ILogger<StrategyRotatorService> _logger;

    private readonly Dictionary<string, DateTimeOffset> _lastRotationAt = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _activeTemplateBySymbol = new(StringComparer.OrdinalIgnoreCase);

    public StrategyRotatorService(
        IStrategyConfigService configService,
        IStrategyEngine engine,
        IOptions<AutoPilotConfig> config,
        ILogger<StrategyRotatorService> logger)
    {
        _configService = configService;
        _engine = engine;
        _config = config.Value;
        _logger = logger;
    }

    public async Task<Result<RotationResult, DomainError>> EvaluateRotationAsync(
        string symbol,
        MarketRegime currentRegime,
        bool isBullish,
        CancellationToken cancellationToken = default)
    {
        if (!_config.Enabled)
            return Result<RotationResult, DomainError>.Success(
                new RotationResult(false, null, null, currentRegime, "AutoPilot deshabilitado"));

        var targetTemplateId = SelectTemplate(currentRegime, isBullish);

        if (targetTemplateId is null)
        {
            if (_config.HighVolatilityAction == "PauseAll")
            {
                var paused = await PauseAllForSymbolAsync(symbol, cancellationToken);
                return Result<RotationResult, DomainError>.Success(
                    new RotationResult(paused, null, null, currentRegime, "HighVolatility — todas pausadas"));
            }
            return Result<RotationResult, DomainError>.Success(
                new RotationResult(false, null, null, currentRegime, "Sin acción para HighVolatility"));
        }

        if (_activeTemplateBySymbol.TryGetValue(symbol, out var currentTemplateId)
            && currentTemplateId == targetTemplateId)
        {
            return Result<RotationResult, DomainError>.Success(
                new RotationResult(false, null, null, currentRegime, "Misma estrategia — sin rotación"));
        }

        if (!IsCooldownElapsed(symbol))
        {
            return Result<RotationResult, DomainError>.Success(
                new RotationResult(false, null, null, currentRegime,
                    $"Cooldown activo ({_config.RotationCooldownMinutes} min)"));
        }

        var deactivatedName = await DeactivateCurrentAsync(symbol, cancellationToken);
        var activatedName = await ActivateTemplateAsync(symbol, targetTemplateId, cancellationToken);

        if (activatedName is not null)
        {
            _activeTemplateBySymbol[symbol] = targetTemplateId;
            _lastRotationAt[symbol] = DateTimeOffset.UtcNow;

            _logger.LogInformation(
                "Rotación completada para {Symbol}: {From} → {To} (régimen: {Regime})",
                symbol, deactivatedName ?? "ninguna", activatedName, currentRegime);
        }

        return Result<RotationResult, DomainError>.Success(
            new RotationResult(
                activatedName is not null,
                deactivatedName,
                activatedName,
                currentRegime,
                $"Rotación: {deactivatedName ?? "—"} → {activatedName ?? "—"}"));
    }

    private string? SelectTemplate(MarketRegime regime, bool isBullish) => regime switch
    {
        MarketRegime.Trending when isBullish  => _config.TrendingTemplateId,
        MarketRegime.Trending when !isBullish => _config.BearishTemplateId,
        MarketRegime.Ranging                  => _config.RangingTemplateId,
        MarketRegime.HighVolatility           => null,
        _                                     => null
    };

    private bool IsCooldownElapsed(string symbol)
    {
        if (!_lastRotationAt.TryGetValue(symbol, out var lastAt))
            return true;

        return DateTimeOffset.UtcNow - lastAt >= TimeSpan.FromMinutes(_config.RotationCooldownMinutes);
    }

    private async Task<string?> DeactivateCurrentAsync(string symbol, CancellationToken ct)
    {
        var allActive = await _configService.GetAllActiveAsync(ct);
        var current = allActive.FirstOrDefault(s =>
            s.Symbol.Value.Equals(symbol, StringComparison.OrdinalIgnoreCase)
            && s.Name.Contains("AutoPilot", StringComparison.OrdinalIgnoreCase));

        if (current is null)
            return null;

        await _configService.DeactivateAsync(current.Id, ct);
        return current.Name;
    }

    private async Task<string?> ActivateTemplateAsync(string symbol, string templateId, CancellationToken ct)
    {
        var template = StrategyTemplateStore.All.FirstOrDefault(t =>
            t.Id.Equals(templateId, StringComparison.OrdinalIgnoreCase));

        if (template is null)
        {
            _logger.LogWarning("Template '{TemplateId}' no encontrado", templateId);
            return null;
        }

        var strategyName = $"AutoPilot — {template.Name} — {symbol}";

        var existingStrategies = await _configService.GetAllAsync(ct);
        var existing = existingStrategies.FirstOrDefault(s =>
            s.Name.Equals(strategyName, StringComparison.OrdinalIgnoreCase));

        if (existing is not null)
        {
            if (!existing.IsActive)
                await _configService.ActivateAsync(existing.Id, ct);
            return strategyName;
        }

        var symbolResult = Core.ValueObjects.Symbol.Create(symbol);
        if (symbolResult.IsFailure) return null;

        var timeframe = Enum.TryParse<CandleInterval>(template.RiskConfig.Timeframe, true, out var tf)
            ? tf : CandleInterval.OneHour;
        CandleInterval? confirmationTf = template.RiskConfig.ConfirmationTimeframe is not null
            && Enum.TryParse<CandleInterval>(template.RiskConfig.ConfirmationTimeframe, true, out var ctf)
            ? ctf : null;

        var riskResult = Core.ValueObjects.RiskConfig.Create(
            template.RiskConfig.MaxOrderAmountUsdt,
            template.RiskConfig.MaxDailyLossUsdt,
            template.RiskConfig.StopLossPercent,
            template.RiskConfig.TakeProfitPercent,
            template.RiskConfig.MaxOpenPositions,
            template.RiskConfig.UseAtrSizing,
            template.RiskConfig.RiskPercentPerTrade,
            template.RiskConfig.AtrMultiplier);

        if (riskResult.IsFailure) return null;

        var strategyResult = Core.Entities.TradingStrategy.Create(
            strategyName, symbolResult.Value, TradingMode.PaperTrading,
            riskResult.Value, $"Creada por AutoPilot desde template {template.Name}",
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

        var createResult = await _configService.CreateAsync(strategy, ct);
        if (createResult.IsFailure) return null;

        var activateResult = await _configService.ActivateAsync(createResult.Value.Id, ct);
        return activateResult.IsSuccess ? strategyName : null;
    }

    private async Task<bool> PauseAllForSymbolAsync(string symbol, CancellationToken ct)
    {
        var allActive = await _configService.GetAllActiveAsync(ct);
        var symbolStrategies = allActive.Where(s =>
            s.Symbol.Value.Equals(symbol, StringComparison.OrdinalIgnoreCase)
            && s.Name.Contains("AutoPilot", StringComparison.OrdinalIgnoreCase));

        var paused = false;
        foreach (var strategy in symbolStrategies)
        {
            await _configService.DeactivateAsync(strategy.Id, ct);
            paused = true;
        }
        return paused;
    }
}
