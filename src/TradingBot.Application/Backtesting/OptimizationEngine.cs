using Microsoft.Extensions.Logging;
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
    decimal                          AveragePnLPerTrade);

/// <summary>Resultado completo de una optimización.</summary>
public sealed record OptimizationResult(
    string                               StrategyName,
    string                               Symbol,
    DateTimeOffset                       From,
    DateTimeOffset                       To,
    int                                  TotalCombinations,
    int                                  CompletedCombinations,
    TimeSpan                             Duration,
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
        CancellationToken cancellationToken = default)
    {
        var startTime = DateTimeOffset.UtcNow;
        var combinations = GenerateCombinations(parameterRanges);

        _logger.LogInformation(
            "Optimización iniciada: {Combinations} combinaciones para '{Name}'",
            combinations.Count, baseStrategy.Name);

        var results = new List<OptimizationRunSummary>();
        var completed = 0;

        foreach (var combo in combinations)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var modifiedStrategy = ApplyParameters(baseStrategy, combo);

            var (tradingStrategy, ruleEngine) = await strategyFactory(modifiedStrategy, cancellationToken);

            // Warm-up
            var maxPeriod = modifiedStrategy.Indicators
                .Select(i => (int)i.GetParameter("period", 14))
                .DefaultIfEmpty(0)
                .Max();

            var warmUpCount = Math.Min(maxPeriod + 10, klines.Count);
            for (var i = 0; i < warmUpCount; i++)
            {
                var k = klines[i];
                var price = Price.Create(k.Close);
                if (price.IsFailure) continue;

                var tick = new Core.Events.MarketTickReceivedEvent(
                    modifiedStrategy.Symbol, price.Value, price.Value, price.Value, k.Volume, k.OpenTime);

                await tradingStrategy.ProcessTickAsync(tick, cancellationToken);
            }

            var backtestKlines = klines.Skip(warmUpCount).ToList();
            if (backtestKlines.Count == 0) continue;

            var backtestResult = await _backtestEngine.RunAsync(
                modifiedStrategy, tradingStrategy, ruleEngine, backtestKlines, cancellationToken);

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
                AveragePnLPerTrade: backtestResult.AveragePnLPerTrade));

            if (completed % 10 == 0)
                _logger.LogDebug(
                    "Optimización: {Completed}/{Total} combinaciones completadas",
                    completed, combinations.Count);
        }

        // Ordenar por P&L descendente y asignar ranking
        var ranked = results
            .OrderByDescending(r => r.TotalPnL)
            .Select((r, i) => r with { Rank = i + 1 })
            .ToList();

        var duration = DateTimeOffset.UtcNow - startTime;

        _logger.LogInformation(
            "Optimización completada en {Duration:N1}s: {Completed} combinaciones, mejor P&L={BestPnL:N2} USDT",
            duration.TotalSeconds, completed,
            ranked.Count > 0 ? ranked[0].TotalPnL : 0m);

        return new OptimizationResult(
            baseStrategy.Name,
            baseStrategy.Symbol.Value,
            klines[0].OpenTime,
            klines[^1].OpenTime,
            combinations.Count,
            completed,
            duration,
            ranked);
    }

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
        // Clonar la estrategia base
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
            riskConfig.MaxOpenPositions).Value;

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

        // Copiar reglas, aplicando amountUsdt si está en los parámetros
        var overrideAmount = parameters.TryGetValue("amountUsdt", out var amt) ? amt : (decimal?)null;

        foreach (var rule in baseStrategy.Rules)
        {
            var action = overrideAmount.HasValue
                ? new RuleAction(rule.Action.Type, overrideAmount.Value, rule.Action.LimitPriceOffsetPercent)
                : rule.Action;

            var ruleResult = TradingRule.Create(
                copy.Id, rule.Name, rule.Type,
                rule.Condition, action);
            if (ruleResult.IsSuccess)
                copy.AddRule(ruleResult.Value);
        }

        return copy;
    }
}
