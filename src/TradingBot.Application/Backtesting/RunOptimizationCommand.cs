using MediatR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TradingBot.Core.Common;
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
    IReadOnlyList<ParameterRange> ParameterRanges) : IRequest<Result<OptimizationResult, DomainError>>;

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
            "Descargando klines para optimización: {Symbol} ({From} → {To})",
            strategy.Symbol.Value, request.From, request.To);

        var klinesResult = await marketDataService.GetKlinesAsync(
            strategy.Symbol, request.From, request.To, cancellationToken);

        if (klinesResult.IsFailure)
            return Result<OptimizationResult, DomainError>.Failure(klinesResult.Error);

        if (klinesResult.Value.Count == 0)
            return Result<OptimizationResult, DomainError>.Failure(
                DomainError.Validation("No se encontraron datos históricos para el rango especificado."));

        // 3. Ejecutar optimización
        var backtestEngine = new BacktestEngine(
            serviceProvider.GetRequiredService<ILogger<BacktestEngine>>());

        var optimizer = new OptimizationEngine(
            backtestEngine,
            serviceProvider.GetRequiredService<ILogger<OptimizationEngine>>());

        var result = await optimizer.RunAsync(
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
            cancellationToken);

        return Result<OptimizationResult, DomainError>.Success(result);
    }
}
