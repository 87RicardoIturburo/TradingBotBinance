using MediatR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TradingBot.Core.Common;
using TradingBot.Core.Entities;
using TradingBot.Core.Enums;
using TradingBot.Core.Interfaces.Services;
using TradingBot.Core.Interfaces.Trading;
using TradingBot.Core.ValueObjects;

namespace TradingBot.Application.Backtesting;

/// <summary>
/// Resultado de un ranking de templates para un symbol.
/// </summary>
public sealed record TemplateRankingResult(
    string                              Symbol,
    DateTimeOffset                      From,
    DateTimeOffset                      To,
    int                                 TotalKlines,
    SymbolProfile                       Profile,
    IReadOnlyList<TemplateRankEntry>    Rankings);

/// <summary>
/// Resultado de un template individual dentro del ranking.
/// </summary>
public sealed record TemplateRankEntry(
    int                         Rank,
    string                      TemplateId,
    string                      TemplateName,
    int                         TotalTrades,
    int                         WinningTrades,
    decimal                     WinRate,
    decimal                     TotalPnL,
    decimal                     MaxDrawdownPercent,
    decimal                     SharpeRatio,
    decimal                     ProfitFactor,
    decimal                     ReturnOnInvestment);

/// <summary>
/// Ejecuta backtest de todos los templates contra un symbol específico.
/// Descarga klines UNA vez, calcula el <see cref="SymbolProfile"/> para adaptar
/// parámetros, y ejecuta cada template en paralelo.
/// </summary>
public sealed record RunTemplateRankingCommand(
    string         SymbolValue,
    int            FromDays     = 30,
    CandleInterval Interval     = CandleInterval.OneHour,
    string         RankBy       = "SharpeRatio") : IRequest<Result<TemplateRankingResult, DomainError>>;

internal sealed class RunTemplateRankingCommandHandler(
    IMarketDataService marketDataService,
    IServiceProvider serviceProvider,
    ILogger<RunTemplateRankingCommandHandler> logger) : IRequestHandler<RunTemplateRankingCommand, Result<TemplateRankingResult, DomainError>>
{
    private static readonly IReadOnlyList<StrategyTemplateDto> Templates = StrategyTemplateStore.All;

    public async Task<Result<TemplateRankingResult, DomainError>> Handle(
        RunTemplateRankingCommand request,
        CancellationToken cancellationToken)
    {
        var symbolResult = Symbol.Create(request.SymbolValue);
        if (symbolResult.IsFailure)
            return Result<TemplateRankingResult, DomainError>.Failure(symbolResult.Error);

        var symbol = symbolResult.Value;
        var to     = DateTimeOffset.UtcNow;
        var from   = to.AddDays(-request.FromDays);

        logger.LogInformation(
            "Ranking de templates: descargando klines {Symbol} ({From:d} → {To:d}) intervalo={Interval}",
            symbol.Value, from, to, request.Interval);

        var klinesResult = await marketDataService.GetKlinesAsync(
            symbol, from, to, request.Interval, cancellationToken);

        if (klinesResult.IsFailure)
            return Result<TemplateRankingResult, DomainError>.Failure(klinesResult.Error);

        var klines = klinesResult.Value;
        if (klines.Count < 50)
            return Result<TemplateRankingResult, DomainError>.Failure(
                DomainError.Validation(
                    $"Se requieren al menos 50 klines para el ranking. Se obtuvieron {klines.Count}."));

        var spreadPercent = GetCurrentSpread(symbol);
        var profile = SymbolProfiler.Analyze(klines, spreadPercent);

        logger.LogInformation(
            "SymbolProfile para {Symbol}: medianATR%={Atr:P2}, medianBW={BW:P2}, " +
            "spread={Spread:P3}, volumeCV={CV:F2} → adjustedATR%={AdjAtr:P2}, adjustedBW={AdjBW:P2}, " +
            "adjustedSpread={AdjSpread:P2}, adjustedVolMinRatio={AdjVol:F1}",
            symbol.Value, profile.MedianAtrPercent, profile.MedianBandWidth,
            profile.CurrentSpreadPercent, profile.VolumeCV,
            profile.AdjustedHighVolatilityAtrPercent, profile.AdjustedHighVolatilityBandWidthPercent,
            profile.AdjustedMaxSpreadPercent, profile.AdjustedVolumeMinRatio);

        var entries = new List<TemplateRankEntry>();

        foreach (var template in Templates)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                var result = await RunSingleTemplateAsync(
                    template, symbol, klines, profile, cancellationToken);

                if (result is not null)
                    entries.Add(result with { Rank = 0 });
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex,
                    "Template '{Name}' falló para {Symbol}: {Message}",
                    template.Name, symbol.Value, ex.Message);
            }
        }

        entries = RankEntries(entries, request.RankBy);

        logger.LogInformation(
            "Ranking completado para {Symbol}: {Count} templates evaluados. " +
            "Mejor: '{Best}' con Sharpe={Sharpe:F2}, PnL={PnL:F2}",
            symbol.Value, entries.Count,
            entries.Count > 0 ? entries[0].TemplateName : "N/A",
            entries.Count > 0 ? entries[0].SharpeRatio : 0m,
            entries.Count > 0 ? entries[0].TotalPnL : 0m);

        return Result<TemplateRankingResult, DomainError>.Success(
            new TemplateRankingResult(symbol.Value, from, to, klines.Count, profile, entries));
    }

    private async Task<TemplateRankEntry?> RunSingleTemplateAsync(
        StrategyTemplateDto template,
        Symbol symbol,
        IReadOnlyList<Kline> klines,
        SymbolProfile profile,
        CancellationToken cancellationToken)
    {
        var riskResult = BuildAdaptedRiskConfig(template.RiskConfig, profile);
        if (riskResult.IsFailure) return null;

        var timeframe = Enum.TryParse<CandleInterval>(template.RiskConfig.Timeframe, true, out var tf)
            ? tf : CandleInterval.OneHour;
        CandleInterval? confirmTf = template.RiskConfig.ConfirmationTimeframe is not null
            && Enum.TryParse<CandleInterval>(template.RiskConfig.ConfirmationTimeframe, true, out var ctf)
                ? ctf : null;

        var strategyResult = TradingStrategy.Create(
            $"{template.Name} — {symbol.Value}",
            symbol,
            TradingMode.PaperTrading,
            riskResult.Value,
            template.Description,
            timeframe,
            confirmTf);

        if (strategyResult.IsFailure) return null;
        var strategy = strategyResult.Value;

        foreach (var ind in template.Indicators)
        {
            if (!Enum.TryParse<IndicatorType>(ind.Type, true, out var indType))
                continue;

            var parameters = new Dictionary<string, decimal>(ind.Parameters);
            if (indType == IndicatorType.Volume)
                parameters["minRatio"] = profile.AdjustedVolumeMinRatio;

            var indicatorResult = IndicatorConfig.Create(indType, parameters);
            if (indicatorResult.IsSuccess)
                strategy.AddIndicator(indicatorResult.Value);
        }

        foreach (var rule in template.Rules)
        {
            if (!Enum.TryParse<RuleType>(rule.RuleType, true, out var ruleType)) continue;
            if (!Enum.TryParse<ConditionOperator>(rule.Operator, true, out var condOp)) continue;
            if (!Enum.TryParse<ActionType>(rule.ActionType, true, out var actionType)) continue;

            var conditions = rule.Conditions
                .Select(c =>
                {
                    Enum.TryParse<IndicatorType>(c.Indicator, true, out var indType);
                    Enum.TryParse<Comparator>(c.Comparator, true, out var comparator);
                    return new LeafCondition(indType, comparator, c.Value);
                })
                .ToArray();

            var condition = new RuleCondition(condOp, conditions);

            var tradingRule = TradingRule.Create(
                strategy.Id, rule.Name, ruleType, condition,
                new RuleAction(actionType, rule.AmountUsdt));

            if (tradingRule.IsSuccess)
                strategy.AddRule(tradingRule.Value);
        }

        if (strategy.Indicators.Count == 0 || strategy.Rules.Count == 0)
            return null;

        var tradingStrategy = serviceProvider.GetRequiredService<ITradingStrategy>();
        await tradingStrategy.InitializeAsync(strategy, cancellationToken);

        var maxPeriod = Strategies.IndicatorWarmUpHelper.GetMaxWarmUpPeriod(strategy.Indicators);

        var warmUpCount = Math.Min(maxPeriod + 10, klines.Count);
        for (var i = 0; i < warmUpCount; i++)
            tradingStrategy.WarmUpOhlc(klines[i].High, klines[i].Low, klines[i].Close, klines[i].Volume);

        if (tradingStrategy is Strategies.DefaultTradingStrategy dts)
            dts.SyncPreviousIndicatorState();

        var backtestKlines = klines.Skip(warmUpCount).ToList();
        if (backtestKlines.Count == 0)
            return null;

        var ruleEngine = serviceProvider.GetRequiredService<IRuleEngine>();
        var engine = serviceProvider.GetRequiredService<BacktestEngine>();

        var result = await engine.RunAsync(
            strategy, tradingStrategy, ruleEngine, backtestKlines, cancellationToken);

        return new TemplateRankEntry(
            Rank: 0,
            TemplateId: template.Id,
            TemplateName: template.Name,
            TotalTrades: result.TotalTrades,
            WinningTrades: result.WinningTrades,
            WinRate: result.WinRate,
            TotalPnL: result.TotalPnL,
            MaxDrawdownPercent: result.MaxDrawdownPercent,
            SharpeRatio: result.Metrics.SharpeRatio,
            ProfitFactor: result.Metrics.ProfitFactor,
            ReturnOnInvestment: result.ReturnOnInvestment);
    }

    private static Result<RiskConfig, DomainError> BuildAdaptedRiskConfig(
        StrategyTemplateRiskConfigDto tplRisk,
        SymbolProfile profile)
    {
        return RiskConfig.Create(
            tplRisk.MaxOrderAmountUsdt,
            tplRisk.MaxDailyLossUsdt,
            tplRisk.StopLossPercent,
            tplRisk.TakeProfitPercent,
            tplRisk.MaxOpenPositions,
            tplRisk.UseAtrSizing,
            tplRisk.RiskPercentPerTrade,
            tplRisk.AtrMultiplier,
            highVolatilityBandWidthPercent: profile.AdjustedHighVolatilityBandWidthPercent,
            highVolatilityAtrPercent: profile.AdjustedHighVolatilityAtrPercent,
            maxSpreadPercent: profile.AdjustedMaxSpreadPercent);
    }

    private decimal GetCurrentSpread(Symbol symbol)
    {
        var bidAsk = marketDataService.GetLastBidAsk(symbol);
        if (bidAsk is null) return 0m;

        var (bid, ask) = bidAsk.Value;
        var mid = (bid.Value + ask.Value) / 2m;
        return mid > 0 ? (ask.Value - bid.Value) / mid * 100m : 0m;
    }

    private static List<TemplateRankEntry> RankEntries(List<TemplateRankEntry> entries, string rankBy)
    {
        var ordered = rankBy.ToLowerInvariant() switch
        {
            "pnl"          => entries.OrderByDescending(e => e.TotalPnL),
            "winrate"      => entries.OrderByDescending(e => e.WinRate),
            "profitfactor" => entries.OrderByDescending(e => e.ProfitFactor),
            _              => entries.OrderByDescending(e => e.SharpeRatio)
        };

        return ordered
            .Select((e, i) => e with { Rank = i + 1 })
            .ToList();
    }
}
