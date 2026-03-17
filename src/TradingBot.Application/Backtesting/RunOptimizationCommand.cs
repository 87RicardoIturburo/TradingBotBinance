using MediatR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TradingBot.Core.Common;
using TradingBot.Core.Enums;
using TradingBot.Core.Interfaces.Repositories;
using TradingBot.Core.Interfaces.Services;
using TradingBot.Core.Interfaces.Trading;
using TradingBot.Core.ValueObjects;

namespace TradingBot.Application.Backtesting;

/// <summary>
/// Ejecuta una optimización de parámetros sobre una estrategia existente.
/// Descarga klines UNA vez y reutiliza para todas las combinaciones.
/// </summary>
public sealed record RunOptimizationCommand(
    Guid                          StrategyId,
    DateTimeOffset                From,
    DateTimeOffset                To,
    IReadOnlyList<ParameterRange> ParameterRanges,
    OptimizationRankBy            RankBy = OptimizationRankBy.PnL,
    CandleInterval                Interval = CandleInterval.OneMinute) : IRequest<Result<OptimizationResult, DomainError>>;

internal sealed class RunOptimizationCommandHandler(
    IStrategyRepository strategyRepository,
    IMarketDataService marketDataService,
    IServiceProvider serviceProvider,
    ILogger<RunOptimizationCommandHandler> logger) : IRequestHandler<RunOptimizationCommand, Result<OptimizationResult, DomainError>>
{
    public async Task<Result<OptimizationResult, DomainError>> Handle(
        RunOptimizationCommand request,
        CancellationToken cancellationToken)
    {
        // 1. Validar
        var strategy = await strategyRepository.GetWithRulesAsync(request.StrategyId, cancellationToken);
        if (strategy is null)
            return Result<OptimizationResult, DomainError>.Failure(
                DomainError.NotFound($"Estrategia '{request.StrategyId}'"));

        if (strategy.Indicators.Count == 0)
            return Result<OptimizationResult, DomainError>.Failure(
                DomainError.Validation("La estrategia debe tener al menos un indicador."));

        if (strategy.Rules.Count == 0)
            return Result<OptimizationResult, DomainError>.Failure(
                DomainError.Validation("La estrategia debe tener al menos una regla."));

        if (request.ParameterRanges.Count == 0)
            return Result<OptimizationResult, DomainError>.Failure(
                DomainError.Validation("Debe especificar al menos un rango de parámetros."));

        // Calcular combinaciones totales
        var totalCombinations = OptimizationEngine.GenerateCombinations(request.ParameterRanges).Count;
        if (totalCombinations > OptimizationEngine.MaxCombinations)
            return Result<OptimizationResult, DomainError>.Failure(
                DomainError.Validation(
                    $"Demasiadas combinaciones ({totalCombinations}). Máximo permitido: {OptimizationEngine.MaxCombinations}. Reducí los rangos o aumentá el step."));

        // 2. Descargar klines UNA vez
        logger.LogInformation(
            "Descargando klines para optimización: {Symbol} ({From} → {To}) intervalo={Interval}",
            strategy.Symbol.Value, request.From, request.To, request.Interval);

        var klinesResult = await marketDataService.GetKlinesAsync(
            strategy.Symbol, request.From, request.To, cancellationToken, request.Interval);

        if (klinesResult.IsFailure)
            return Result<OptimizationResult, DomainError>.Failure(klinesResult.Error);

        if (klinesResult.Value.Count == 0)
            return Result<OptimizationResult, DomainError>.Failure(
                DomainError.Validation("No se encontraron datos históricos para el rango especificado."));

        // Validar calidad de datos antes de correr 500 combinaciones
        var klines = klinesResult.Value;
        var maxJump = 0m;
        for (var idx = 1; idx < Math.Min(klines.Count, 200); idx++)
        {
            var prev = klines[idx - 1].Close;
            var curr = klines[idx].Close;
            if (prev > 0) maxJump = Math.Max(maxJump, Math.Abs(curr - prev) / prev * 100m);
        }
        logger.LogInformation(
            "Calidad de datos: {Count} velas | Precio inicial={First:F2} | Precio final={Last:F2} | "
            + "Salto máximo entre velas (muestra 200)={MaxJump:F2}%",
            klines.Count, klines[0].Close, klines[^1].Close, maxJump);
        if (maxJump > 20m)
            logger.LogWarning(
                "⚠ CALIDAD DE DATOS: salto extremo de {MaxJump:F2}% entre velas consecutivas. "
                + "Datos sintéticos del entorno Demo producirán P&L irreal en todas las combinaciones. "
                + "Probá con un período diferente o usá Testnet en lugar de Demo. "
                + "Precios observados: {First:F2} → {Last:F2} USDT",
                maxJump, klines[0].Close, klines[^1].Close);

        // 3. Ejecutar optimización
        var backtestEngine = serviceProvider.GetRequiredService<BacktestEngine>();

        var optimizer = new OptimizationEngine(
            backtestEngine,
            serviceProvider.GetRequiredService<ILogger<OptimizationEngine>>());

        var result = await optimizer.RunAsync(
            strategy,
            request.ParameterRanges,
            klines,
            async (modifiedStrategy, ct) =>
            {
                var tradingStrategy = serviceProvider.GetRequiredService<ITradingStrategy>();
                await tradingStrategy.InitializeAsync(modifiedStrategy, ct);
                var ruleEngine = serviceProvider.GetRequiredService<IRuleEngine>();
                return (tradingStrategy, ruleEngine);
            },
            request.RankBy,
            cancellationToken);

        return Result<OptimizationResult, DomainError>.Success(result);
    }
}

/// <summary>
/// Ejecuta walk-forward analysis: optimiza en 70% train, valida en 30% test.
/// Detecta overfitting si la degradación de métricas supera el 30%.
/// </summary>
public sealed record RunWalkForwardCommand(
    Guid                          StrategyId,
    DateTimeOffset                From,
    DateTimeOffset                To,
    IReadOnlyList<ParameterRange> ParameterRanges,
    OptimizationRankBy            RankBy = OptimizationRankBy.SharpeRatio,
    CandleInterval                Interval = CandleInterval.OneMinute) : IRequest<Result<WalkForwardResult, DomainError>>;

internal sealed class RunWalkForwardCommandHandler(
    IStrategyRepository strategyRepository,
    IMarketDataService marketDataService,
    IServiceProvider serviceProvider,
    ILogger<RunWalkForwardCommandHandler> logger) : IRequestHandler<RunWalkForwardCommand, Result<WalkForwardResult, DomainError>>
{
    public async Task<Result<WalkForwardResult, DomainError>> Handle(
        RunWalkForwardCommand request,
        CancellationToken cancellationToken)
    {
        var strategy = await strategyRepository.GetWithRulesAsync(request.StrategyId, cancellationToken);
        if (strategy is null)
            return Result<WalkForwardResult, DomainError>.Failure(
                DomainError.NotFound($"Estrategia '{request.StrategyId}'"));

        if (strategy.Indicators.Count == 0 || strategy.Rules.Count == 0)
            return Result<WalkForwardResult, DomainError>.Failure(
                DomainError.Validation("La estrategia debe tener al menos un indicador y una regla."));

        if (request.ParameterRanges.Count == 0)
            return Result<WalkForwardResult, DomainError>.Failure(
                DomainError.Validation("Debe especificar al menos un rango de parámetros."));

        var klinesResult = await marketDataService.GetKlinesAsync(
            strategy.Symbol, request.From, request.To, cancellationToken, request.Interval);

        if (klinesResult.IsFailure)
            return Result<WalkForwardResult, DomainError>.Failure(klinesResult.Error);

        if (klinesResult.Value.Count < 100)
            return Result<WalkForwardResult, DomainError>.Failure(
                DomainError.Validation("Se requieren al menos 100 klines para walk-forward analysis."));

        var backtestEngine = serviceProvider.GetRequiredService<BacktestEngine>();
        var optimizer = new OptimizationEngine(
            backtestEngine,
            serviceProvider.GetRequiredService<ILogger<OptimizationEngine>>());

        try
        {
            var result = await optimizer.RunWalkForwardAsync(
                strategy,
                request.ParameterRanges,
                klinesResult.Value,
                async (modifiedStrategy, ct) =>
                {
                    var tradingStrategy = serviceProvider.GetRequiredService<ITradingStrategy>();
                    await tradingStrategy.InitializeAsync(modifiedStrategy, ct);
                    var ruleEngine = serviceProvider.GetRequiredService<IRuleEngine>();
                    return (tradingStrategy, ruleEngine);
                },
                request.RankBy,
                cancellationToken: cancellationToken);

            return Result<WalkForwardResult, DomainError>.Success(result);
        }
        catch (InvalidOperationException ex)
        {
            return Result<WalkForwardResult, DomainError>.Failure(
                DomainError.Validation(ex.Message));
        }
    }
}
