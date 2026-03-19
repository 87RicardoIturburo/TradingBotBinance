using Microsoft.Extensions.Logging;
using TradingBot.Application.Strategies;
using TradingBot.Core.Common;
using TradingBot.Core.Entities;
using TradingBot.Core.Enums;
using TradingBot.Core.Interfaces.Services;
using TradingBot.Core.Interfaces.Trading;
using TradingBot.Core.ValueObjects;

namespace TradingBot.Application.Backtesting;

/// <summary>
/// Rango de valores para un parámetro a optimizar.
/// Genera valores desde <see cref="Min"/> hasta <see cref="Max"/> con paso <see cref="Step"/>.
/// </summary>
public sealed record ParameterRange(
    string  Name,
    decimal Min,
    decimal Max,
    decimal Step)
{
    public IReadOnlyList<decimal> GenerateValues()
    {
        var values = new List<decimal>();
        for (var v = Min; v <= Max; v += Step)
            values.Add(v);
        return values;
    }

    public int ValueCount => Step <= 0 ? 0 : (int)((Max - Min) / Step) + 1;
}

/// <summary>Métrica por la cual se ordena el ranking de la optimización.</summary>
public enum OptimizationRankBy
{
    PnL,
    SharpeRatio,
    SortinoRatio,
    CalmarRatio,
    ProfitFactor
}

/// <summary>Resultado resumido de una combinación de parámetros.</summary>
public sealed record OptimizationRunSummary(
    int                              Rank,
    Dictionary<string, decimal>      Parameters,
    int                              TotalTrades,
    int                              WinningTrades,
    decimal                          WinRate,
    decimal                          TotalPnL,
    decimal                          TotalInvested,
    decimal                          ReturnOnInvestment,
    decimal                          MaxDrawdownPercent,
    decimal                          AveragePnLPerTrade,
    BacktestMetrics                  Metrics);

/// <summary>Resultado completo de una optimización.</summary>
public sealed record OptimizationResult(
    string                               StrategyName,
    string                               Symbol,
    DateTimeOffset                       From,
    DateTimeOffset                       To,
    int                                  TotalCombinations,
    int                                  CompletedCombinations,
    TimeSpan                             Duration,
    OptimizationRankBy                   RankedBy,
    IReadOnlyList<OptimizationRunSummary> Results);

/// <summary>
/// Motor de optimización. Genera combinaciones de parámetros y ejecuta
/// backtests en secuencia reutilizando las mismas klines.
/// </summary>
internal sealed class OptimizationEngine
{
    private readonly BacktestEngine _backtestEngine;
    private readonly ILogger<OptimizationEngine> _logger;

    internal const int MaxCombinations = 500;

    public OptimizationEngine(
        BacktestEngine backtestEngine,
        ILogger<OptimizationEngine> logger)
    {
        _backtestEngine = backtestEngine;
        _logger = logger;
    }

    /// <summary>
    /// Ejecuta la optimización con todas las combinaciones de parámetros.
    /// Reutiliza las klines descargadas para cada combinación.
    /// </summary>
    public async Task<OptimizationResult> RunAsync(
        TradingStrategy baseStrategy,
        IReadOnlyList<ParameterRange> parameterRanges,
        IReadOnlyList<Kline> klines,
        Func<TradingStrategy, CancellationToken, Task<(ITradingStrategy, IRuleEngine)>> strategyFactory,
        OptimizationRankBy rankBy = OptimizationRankBy.PnL,
        CancellationToken cancellationToken = default)
    {
        var startTime = DateTimeOffset.UtcNow;
        var combinations = GenerateCombinations(parameterRanges);

        _logger.LogInformation(
            "Optimización iniciada: {Combinations} combinaciones para '{Name}'",
            combinations.Count, baseStrategy.Name);

        var results = new List<OptimizationRunSummary>();
        var completed = 0;
        BacktestResult? firstResult = null;

        foreach (var combo in combinations)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var modifiedStrategy = ApplyParameters(baseStrategy, combo);

            // Log de los parámetros efectivos aplicados a cada combinación
            var paramsLog = string.Join(", ", combo.Select(kv => $"{kv.Key}={kv.Value}"));
            var indicatorsLog = string.Join(", ", modifiedStrategy.Indicators.Select(
                ind => $"{ind.Type}({string.Join(",", ind.Parameters.Select(p => $"{p.Key}={p.Value}"))})"));
            _logger.LogDebug(
                "Optimización combo [{N}/{Total}]: params=[{Params}] indicadores=[{Indicators}] SL={SL}% TP={TP}%",
                completed + 1, combinations.Count, paramsLog, indicatorsLog,
                modifiedStrategy.RiskConfig.StopLossPercent.Value,
                modifiedStrategy.RiskConfig.TakeProfitPercent.Value);

            var (tradingStrategy, ruleEngine) = await strategyFactory(modifiedStrategy, cancellationToken);

            // Warm-up: alimentar indicadores SIN evaluar señales para no contaminar
            // _lastSignalAt ni _previousRsi con señales fantasma del warm-up
            var maxPeriod = modifiedStrategy.Indicators
                .Select(i => (int)i.GetParameter("period", 14))
                .DefaultIfEmpty(0)
                .Max();

            var warmUpCount = Math.Min(maxPeriod + 10, klines.Count);
            for (var i = 0; i < warmUpCount; i++)
                tradingStrategy.WarmUpOhlc(klines[i].High, klines[i].Low, klines[i].Close, klines[i].Volume);

            // Sincronizar estado previo de RSI/MACD para evitar señales falsas
            // en el primer tick del backtest (sin setear cooldown)
            if (tradingStrategy is DefaultTradingStrategy dts)
                dts.SyncPreviousIndicatorState();

            var backtestKlines = klines.Skip(warmUpCount).ToList();
            if (backtestKlines.Count == 0) continue;

            var backtestResult = await _backtestEngine.RunAsync(
                modifiedStrategy, tradingStrategy, ruleEngine, backtestKlines, cancellationToken);

            // Log diagnóstico por combinación: desglose de razones de cierre con P&L promedio
            var exitBreakdown = backtestResult.Trades.Count > 0
                ? string.Join(" | ", backtestResult.Trades
                    .GroupBy(t => t.ExitReason)
                    .Select(g => $"{g.Key}×{g.Count()}(avg={g.Average(t => t.NetPnL):F4} USDT)"))
                : "sin trades";

            _logger.LogInformation(
                "Combo [{N}/{Total}] [{Params}] → {Trades} trades | "
                + "Win={Wins}/{Trades} ({WR:F0}%) | P&L={PnL:F4} USDT | [{Exits}]",
                completed + 1, combinations.Count, paramsLog,
                backtestResult.TotalTrades, backtestResult.WinningTrades, backtestResult.WinRate,
                backtestResult.TotalPnL, exitBreakdown);

            firstResult ??= backtestResult;
            completed++;
            results.Add(new OptimizationRunSummary(
                Rank: 0, // se asigna después de ordenar
                Parameters: combo,
                TotalTrades: backtestResult.TotalTrades,
                WinningTrades: backtestResult.WinningTrades,
                WinRate: backtestResult.WinRate,
                TotalPnL: backtestResult.TotalPnL,
                TotalInvested: backtestResult.TotalInvested,
                ReturnOnInvestment: backtestResult.ReturnOnInvestment,
                MaxDrawdownPercent: backtestResult.MaxDrawdownPercent,
                AveragePnLPerTrade: backtestResult.AveragePnLPerTrade,
                Metrics: backtestResult.Metrics));

            if (completed % 10 == 0)
                _logger.LogDebug(
                    "Optimización: {Completed}/{Total} combinaciones completadas",
                    completed, combinations.Count);
        }

        // Ordenar por la métrica seleccionada y asignar ranking
        var ranked = results
            .OrderByDescending(r => GetRankingValue(r, rankBy))
            .Select((r, i) => r with { Rank = i + 1 })
            .ToList();

        // Diagnóstico final: alertar si la mayoría de combinaciones son negativas
        if (ranked.Count >= 5)
        {
            var negativeCount  = ranked.Count(r => r.TotalTrades > 0 && r.TotalPnL < 0);
            var noTradesCount  = ranked.Count(r => r.TotalTrades == 0);
            var positiveCount  = ranked.Count(r => r.TotalPnL > 0);

            if (negativeCount > ranked.Count * 0.8m)
            {
                _logger.LogWarning(
                    "⚠ DIAGNÓSTICO OPTIMIZADOR: {Neg}/{Total} combos con P&L negativo, "
                    + "{Zero} sin trades, {Pos} positivos. "
                    + "Causas probables: (1) período de mercado bajista/lateral, "
                    + "(2) SL demasiado ajustado vs volatilidad del activo, "
                    + "(3) reglas de salida cierran antes del TP. "
                    + "Activá logging DEBUG para ver snapshot de indicadores en cada ENTRADA/SALIDA.",
                    negativeCount, ranked.Count, noTradesCount, positiveCount);
            }
        }

        var duration = DateTimeOffset.UtcNow - startTime;

        _logger.LogInformation(
            "Optimización completada en {Duration:N1}s: {Completed} combinaciones, RankBy={RankBy}, mejor P&L={BestPnL:N2} USDT",
            duration.TotalSeconds, completed, rankBy,
            ranked.Count > 0 ? ranked[0].TotalPnL : 0m);

        // Diagnóstico: detectar cuando todas las combinaciones producen resultados idénticos
        if (ranked.Count > 1)
        {
            var distinctTrades = ranked.Select(r => r.TotalTrades).Distinct().Count();
            var distinctPnL = ranked.Select(r => r.TotalPnL).Distinct().Count();

            if (distinctTrades == 1 && distinctPnL == 1)
            {
                var trades = ranked[0].TotalTrades;
                if (trades == 0)
                {
                    _logger.LogWarning(
                        "⚠ Todas las {Count} combinaciones produjeron 0 trades. Posibles causas: " +
                        "(1) RSI nunca cruza los umbrales oversold/overbought en el rango de datos, " +
                        "(2) régimen HighVolatility suprime todas las señales (ADX/BB/ATR presentes), " +
                        "(3) confirmación multi-indicador rechaza todas las señales, " +
                        "(4) rango de fechas demasiado corto",
                        ranked.Count);
                }
                else
                {
                    // Diagnosticar razones de cierre del primer backtest
                    var exitReasonsSummary = firstResult?.Trades
                        .GroupBy(t => t.ExitReason)
                        .Select(g => $"{g.Key}×{g.Count()}")
                        .DefaultIfEmpty("N/A");

                    _logger.LogWarning(
                        "⚠ Todas las {Count} combinaciones produjeron resultados idénticos " +
                        "({Trades} trades, P&L={PnL:N4}). " +
                        "Razones de cierre: [{ExitReasons}]. " +
                        "Si todos cierran por 'Exit rule' (RSI), el período del RSI no cambia los cruces " +
                        "suficientemente. Si cierran por SL/TP, los % son demasiado similares al precio de entrada. " +
                        "Intentá variar RSI.oversold (ej: 25-40) o ampliar el rango de fechas.",
                        ranked.Count, trades, ranked[0].TotalPnL,
                        string.Join(", ", exitReasonsSummary!));
                }
            }
        }

        return new OptimizationResult(
            baseStrategy.Name,
            baseStrategy.Symbol.Value,
            klines[0].OpenTime,
            klines[^1].OpenTime,
            combinations.Count,
            completed,
            duration,
            rankBy,
            ranked);
    }

    /// <summary>Obtiene el valor de ranking según la métrica seleccionada.</summary>
    private static decimal GetRankingValue(OptimizationRunSummary r, OptimizationRankBy rankBy) => rankBy switch
    {
        OptimizationRankBy.SharpeRatio  => r.Metrics.SharpeRatio,
        OptimizationRankBy.SortinoRatio => r.Metrics.SortinoRatio,
        OptimizationRankBy.CalmarRatio  => r.Metrics.CalmarRatio,
        OptimizationRankBy.ProfitFactor => r.Metrics.ProfitFactor,
        _                              => r.TotalPnL
    };

    /// <summary>
    /// Genera todas las combinaciones de parámetros como producto cartesiano.
    /// </summary>
    internal static IReadOnlyList<Dictionary<string, decimal>> GenerateCombinations(
        IReadOnlyList<ParameterRange> ranges)
    {
        var result = new List<Dictionary<string, decimal>> { new() };

        foreach (var range in ranges)
        {
            var values = range.GenerateValues();
            var expanded = new List<Dictionary<string, decimal>>();

            foreach (var existing in result)
            {
                foreach (var value in values)
                {
                    var combo = new Dictionary<string, decimal>(existing)
                    {
                        [range.Name] = value
                    };
                    expanded.Add(combo);
                }
            }

            result = expanded;
        }

        return result;
    }

    /// <summary>
    /// Crea una copia en memoria de la estrategia con los parámetros modificados.
    /// Soporta parámetros de indicadores (ej: "RSI.period"), risk config
    /// (ej: "stopLossPercent", "maxDailyLossUsdt", "maxOrderAmountUsdt")
    /// y reglas (ej: "amountUsdt").
    /// </summary>
    private static TradingStrategy ApplyParameters(
        TradingStrategy baseStrategy,
        Dictionary<string, decimal> parameters)
    {
        // Clonar la estrategia base preservando TODOS los campos de RiskConfig
        var riskConfig = baseStrategy.RiskConfig;
        var slPercent = (decimal)riskConfig.StopLossPercent;
        var tpPercent = (decimal)riskConfig.TakeProfitPercent;
        var maxOrder = riskConfig.MaxOrderAmountUsdt;
        var maxDailyLoss = riskConfig.MaxDailyLossUsdt;

        // Aplicar parámetros de risk config si están presentes
        if (parameters.TryGetValue("stopLossPercent", out var sl)) slPercent = sl;
        if (parameters.TryGetValue("takeProfitPercent", out var tp)) tpPercent = tp;
        if (parameters.TryGetValue("maxOrderAmountUsdt", out var mo)) maxOrder = mo;
        if (parameters.TryGetValue("maxDailyLossUsdt", out var mdl)) maxDailyLoss = mdl;

        var newRisk = RiskConfig.Create(
            maxOrder, maxDailyLoss,
            slPercent, tpPercent,
            riskConfig.MaxOpenPositions,
            riskConfig.UseAtrSizing,
            riskConfig.RiskPercentPerTrade,
            riskConfig.AtrMultiplier,
            riskConfig.UseTrailingStop,
            riskConfig.TrailingStopPercent,
            riskConfig.MaxSpreadPercent).Value;

        var copy = TradingStrategy.Create(
            baseStrategy.Name, baseStrategy.Symbol,
            baseStrategy.Mode, newRisk, baseStrategy.Description).Value;

        // Copiar y modificar indicadores
        foreach (var indicator in baseStrategy.Indicators)
        {
            var newParams = new Dictionary<string, decimal>(indicator.Parameters);
            var prefix = indicator.Type.ToString() + ".";

            foreach (var (key, value) in parameters)
            {
                if (key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    var paramName = key[prefix.Length..];
                    newParams[paramName] = value;
                }
            }

            var configResult = IndicatorConfig.Create(indicator.Type, newParams);
            if (configResult.IsSuccess)
                copy.AddIndicator(configResult.Value);
            else
                copy.AddIndicator(indicator);
        }

        var overrideAmount = parameters.TryGetValue("amountUsdt", out var amt) ? amt : (decimal?)null;

        foreach (var rule in baseStrategy.Rules)
        {
            var action = overrideAmount.HasValue
                ? new RuleAction(rule.Action.Type, overrideAmount.Value, rule.Action.LimitPriceOffsetPercent)
                : rule.Action;

            var updatedCondition = rule.Type == RuleType.Entry
                ? RebuildCondition(rule.Condition, parameters)
                : rule.Condition;

            var ruleResult = TradingRule.Create(
                copy.Id, rule.Name, rule.Type,
                updatedCondition, action);
            if (ruleResult.IsSuccess)
                copy.AddRule(ruleResult.Value);
        }

        return copy;
    }

    /// <summary>
    /// Reconstruye un árbol de condiciones sincronizando los umbrales de indicadores
    /// con los parámetros de la combinación actual del optimizador.
    /// Ejemplo: RSI.oversold=25 actualiza todas las hojas (RSI, LessThan, X) → (RSI, LessThan, 25).
    /// </summary>
    private static RuleCondition RebuildCondition(RuleCondition condition, Dictionary<string, decimal> parameters)
    {
        var newLeaves = condition.Conditions
            .Select(leaf => RebuildLeaf(leaf, parameters))
            .ToArray();
        return condition with { Conditions = newLeaves };
    }

    /// <summary>
    /// Actualiza el umbral de una condición atómica si el parámetro de optimización
    /// correspondiente (oversold / overbought) está en la combinación.
    /// </summary>
    private static LeafCondition RebuildLeaf(LeafCondition leaf, Dictionary<string, decimal> parameters)
    {
        var indicatorName = leaf.Indicator.ToString();

        // oversold → condiciones LessThan / LessThanOrEqual / CrossBelow
        if (leaf.Comparator is Comparator.LessThan or Comparator.LessThanOrEqual or Comparator.CrossBelow
            && parameters.TryGetValue($"{indicatorName}.oversold", out var oversoldValue))
        {
            return leaf with { Value = oversoldValue };
        }

        // overbought → condiciones GreaterThan / GreaterThanOrEqual / CrossAbove
        if (leaf.Comparator is Comparator.GreaterThan or Comparator.GreaterThanOrEqual or Comparator.CrossAbove
            && parameters.TryGetValue($"{indicatorName}.overbought", out var overboughtValue))
        {
            return leaf with { Value = overboughtValue };
        }

        return leaf;
    }

    /// <summary>
    /// Walk-forward analysis: divide klines en 70% train / 30% test.
    /// Optimiza en train, valida el mejor resultado en test.
    /// Reporta degradación de métricas entre train y test (señal de overfitting).
    /// </summary>
    public async Task<WalkForwardResult> RunWalkForwardAsync(
        TradingStrategy baseStrategy,
        IReadOnlyList<ParameterRange> parameterRanges,
        IReadOnlyList<Kline> klines,
        Func<TradingStrategy, CancellationToken, Task<(ITradingStrategy, IRuleEngine)>> strategyFactory,
        OptimizationRankBy rankBy = OptimizationRankBy.SharpeRatio,
        decimal trainRatio = 0.7m,
        CancellationToken cancellationToken = default)
    {
        var splitIndex = (int)(klines.Count * trainRatio);
        if (splitIndex < 50 || klines.Count - splitIndex < 20)
        {
            _logger.LogWarning(
                "Walk-forward: datos insuficientes (train={Train}, test={Test}). Se requieren al menos 50/20 klines.",
                splitIndex, klines.Count - splitIndex);
            throw new InvalidOperationException(
                $"Datos insuficientes para walk-forward: train={splitIndex}, test={klines.Count - splitIndex} klines.");
        }

        var trainKlines = klines.Take(splitIndex).ToList();
        var testKlines  = klines.Skip(splitIndex).ToList();

        _logger.LogInformation(
            "Walk-forward: {Train} klines train / {Test} klines test ({TrainPct:N0}%/{TestPct:N0}%)",
            trainKlines.Count, testKlines.Count,
            trainRatio * 100, (1 - trainRatio) * 100);

        // 1. Optimizar en train
        var trainResult = await RunAsync(
            baseStrategy, parameterRanges, trainKlines, strategyFactory,
            rankBy, cancellationToken);

        if (trainResult.Results.Count == 0)
            throw new InvalidOperationException("Walk-forward: la optimización en train no produjo resultados.");

        var bestParams = trainResult.Results[0].Parameters;
        var trainMetrics = trainResult.Results[0].Metrics;
        var trainPnL = trainResult.Results[0].TotalPnL;

        _logger.LogInformation(
            "Walk-forward: mejor combinación en train — PnL={PnL:N2}, Sharpe={Sharpe:N2}",
            trainPnL, trainMetrics.SharpeRatio);

        // 2. Ejecutar la mejor combinación en test (out-of-sample)
        var testStrategy = ApplyParameters(baseStrategy, bestParams);
        var (testTradingStrategy, testRuleEngine) = await strategyFactory(testStrategy, cancellationToken);

        // Warm-up con datos de train para que los indicadores estén precalentados
        var maxPeriod = testStrategy.Indicators
            .Select(i => (int)i.GetParameter("period", 14))
            .DefaultIfEmpty(0)
            .Max();
        var warmUpCount = Math.Min(maxPeriod + 10, trainKlines.Count);
        for (var i = Math.Max(0, trainKlines.Count - warmUpCount); i < trainKlines.Count; i++)
            testTradingStrategy.WarmUpPrice(trainKlines[i].Close);

        if (testTradingStrategy is Strategies.DefaultTradingStrategy dts)
            dts.SyncPreviousIndicatorState();

        var testBacktest = await _backtestEngine.RunAsync(
            testStrategy, testTradingStrategy, testRuleEngine, testKlines, cancellationToken);

        var testMetrics = testBacktest.Metrics;

        // 3. Calcular degradación
        var trainRankValue = GetRankingValue(trainResult.Results[0], rankBy);
        var testRankValue = rankBy switch
        {
            OptimizationRankBy.SharpeRatio  => testMetrics.SharpeRatio,
            OptimizationRankBy.SortinoRatio => testMetrics.SortinoRatio,
            OptimizationRankBy.CalmarRatio  => testMetrics.CalmarRatio,
            OptimizationRankBy.ProfitFactor => testMetrics.ProfitFactor,
            _                              => testBacktest.TotalPnL
        };

        var degradation = trainRankValue != 0
            ? (1m - testRankValue / trainRankValue) * 100m
            : 0m;

        var isOverfit = degradation > 30m;

        _logger.LogInformation(
            "Walk-forward completado: train {TrainMetric:N2} → test {TestMetric:N2} ({RankBy}), degradación={Deg:N1}% {Warn}",
            trainRankValue, testRankValue, rankBy, degradation,
            isOverfit ? "⚠️ OVERFIT WARNING" : "✓");

        return new WalkForwardResult(
            BestParameters: bestParams,
            TrainPnL: trainPnL,
            TestPnL: testBacktest.TotalPnL,
            TrainMetrics: trainMetrics,
            TestMetrics: testMetrics,
            TrainKlines: trainKlines.Count,
            TestKlines: testKlines.Count,
            DegradationPercent: Math.Round(degradation, 2),
            IsOverfit: isOverfit,
            RankedBy: rankBy);
    }
}

/// <summary>Resultado de un walk-forward analysis (train/test split).</summary>
public sealed record WalkForwardResult(
    Dictionary<string, decimal> BestParameters,
    decimal                     TrainPnL,
    decimal                     TestPnL,
    BacktestMetrics             TrainMetrics,
    BacktestMetrics             TestMetrics,
    int                         TrainKlines,
    int                         TestKlines,
    decimal                     DegradationPercent,
    bool                        IsOverfit,
    OptimizationRankBy          RankedBy);
