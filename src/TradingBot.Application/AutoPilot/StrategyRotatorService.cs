using System.Collections.Concurrent;
using MediatR;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TradingBot.Application.Backtesting;
using TradingBot.Core.Common;
using TradingBot.Core.Entities;
using TradingBot.Core.Enums;
using TradingBot.Core.Interfaces.Repositories;
using TradingBot.Core.Interfaces.Services;
using TradingBot.Core.ValueObjects;

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
    private readonly IPositionRepository _positionRepository;
    private readonly IOrderService _orderService;
    private readonly ISender _mediator;
    private readonly AutoPilotConfig _config;
    private readonly ILogger<StrategyRotatorService> _logger;

    private readonly ConcurrentDictionary<string, DateTimeOffset> _lastRotationAt = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, string> _activeTemplateBySymbol = new(StringComparer.OrdinalIgnoreCase);

    public StrategyRotatorService(
        IStrategyConfigService configService,
        IStrategyEngine engine,
        IPositionRepository positionRepository,
        IOrderService orderService,
        ISender mediator,
        IOptions<AutoPilotConfig> config,
        ILogger<StrategyRotatorService> logger)
    {
        _configService = configService;
        _engine = engine;
        _positionRepository = positionRepository;
        _orderService = orderService;
        _mediator = mediator;
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

        if (_config.ClosePositionsOnRotation)
            await CloseOpenPositionsAsync(current, ct);

        await _configService.DeactivateAsync(current.Id, ct);
        return current.Name;
    }

    private async Task CloseOpenPositionsAsync(TradingStrategy strategy, CancellationToken ct)
    {
        var openPositions = await _positionRepository.GetOpenByStrategyIdAsync(strategy.Id, ct);
        if (openPositions.Count == 0)
            return;

        _logger.LogInformation(
            "AutoPilot cerrando {Count} posiciones abiertas de '{Strategy}' antes de rotar",
            openPositions.Count, strategy.Name);

        foreach (var position in openPositions)
        {
            var closeSide = position.Side == OrderSide.Buy ? OrderSide.Sell : OrderSide.Buy;
            var orderResult = Order.Create(
                strategy.Id, position.Symbol, closeSide,
                OrderType.Market, position.Quantity, strategy.Mode);

            if (orderResult.IsFailure)
            {
                _logger.LogWarning(
                    "No se pudo crear orden de cierre para posición {PositionId}: {Error}",
                    position.Id, orderResult.Error.Message);
                continue;
            }

            var placeResult = await _orderService.PlaceOrderAsync(orderResult.Value, ct);
            if (placeResult.IsFailure)
            {
                _logger.LogWarning(
                    "No se pudo cerrar posición {PositionId}: {Error}",
                    position.Id, placeResult.Error.Message);
            }
        }
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

        if (!await IsTemplateProfitableAsync(symbol, templateId, ct))
            return null;

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

        var mode = Enum.TryParse<TradingMode>(_config.DefaultTradingMode, true, out var m)
            ? m : TradingMode.PaperTrading;

        var strategyResult = Core.Entities.TradingStrategy.Create(
            strategyName, symbolResult.Value, mode,
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

    private async Task<bool> IsTemplateProfitableAsync(string symbol, string templateId, CancellationToken ct)
    {
        try
        {
            var rankingResult = await _mediator.Send(
                new RunTemplateRankingCommand(symbol, FromDays: 7), ct);

            if (rankingResult.IsFailure)
            {
                _logger.LogWarning(
                    "AutoPilot: no se pudo ejecutar backtest rápido para {Symbol}, permitiendo activación",
                    symbol);
                return true;
            }

            var entry = rankingResult.Value.Rankings
                .FirstOrDefault(r => r.TemplateId.Equals(templateId, StringComparison.OrdinalIgnoreCase));

            if (entry is null)
            {
                _logger.LogWarning(
                    "AutoPilot: template '{TemplateId}' no encontrado en ranking de {Symbol}",
                    templateId, symbol);
                return false;
            }

            if (entry.TotalPnL <= 0 || entry.SharpeRatio < 0)
            {
                _logger.LogWarning(
                    "AutoPilot: template '{TemplateId}' no rentable para {Symbol} " +
                    "(PnL={PnL:F2}, Sharpe={Sharpe:F2}). Rotación cancelada.",
                    templateId, symbol, entry.TotalPnL, entry.SharpeRatio);
                return false;
            }

            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "AutoPilot: error en backtest de validación para {Symbol}. Permitiendo activación.",
                symbol);
            return true;
        }
    }
}
