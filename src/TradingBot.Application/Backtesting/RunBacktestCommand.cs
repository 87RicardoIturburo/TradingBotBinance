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
/// Ejecuta un backtest de una estrategia existente contra datos históricos de Binance.
/// Los datos se descargan en memoria y no se persisten.
/// </summary>
public sealed record RunBacktestCommand(
    Guid           StrategyId,
    DateTimeOffset From,
    DateTimeOffset To) : IRequest<Result<BacktestResult, DomainError>>;

internal sealed class RunBacktestCommandHandler(
    IStrategyRepository strategyRepository,
    IMarketDataService marketDataService,
    IServiceProvider serviceProvider,
    ILogger<RunBacktestCommandHandler> logger) : IRequestHandler<RunBacktestCommand, Result<BacktestResult, DomainError>>
{
    public async Task<Result<BacktestResult, DomainError>> Handle(
        RunBacktestCommand request,
        CancellationToken cancellationToken)
    {
        // 1. Cargar la estrategia con reglas e indicadores
        var strategy = await strategyRepository.GetWithRulesAsync(request.StrategyId, cancellationToken);
        if (strategy is null)
            return Result<BacktestResult, DomainError>.Failure(
                DomainError.NotFound($"Estrategia '{request.StrategyId}'"));

        if (strategy.Indicators.Count == 0)
            return Result<BacktestResult, DomainError>.Failure(
                DomainError.Validation("La estrategia debe tener al menos un indicador para hacer backtest."));

        if (strategy.Rules.Count == 0)
            return Result<BacktestResult, DomainError>.Failure(
                DomainError.Validation("La estrategia debe tener al menos una regla para hacer backtest."));

        // 2. Descargar klines históricas de Binance REST
        logger.LogInformation(
            "Descargando klines para backtest: {Symbol} ({From} → {To})",
            strategy.Symbol.Value, request.From, request.To);

        var klinesResult = await marketDataService.GetKlinesAsync(
            strategy.Symbol, request.From, request.To, cancellationToken);

        if (klinesResult.IsFailure)
            return Result<BacktestResult, DomainError>.Failure(klinesResult.Error);

        if (klinesResult.Value.Count == 0)
            return Result<BacktestResult, DomainError>.Failure(
                DomainError.Validation("No se encontraron datos históricos para el rango especificado."));

        // 3. Crear instancia fresca de ITradingStrategy para el backtest
        var tradingStrategy = serviceProvider.GetRequiredService<ITradingStrategy>();
        await tradingStrategy.InitializeAsync(strategy, cancellationToken);

        // 4. Pre-calentar indicadores SIN evaluar señales (evita contaminar
        //    _lastSignalAt y _previousRsi con señales fantasma del warm-up)
        var maxPeriod = strategy.Indicators
            .Select(i => (int)i.GetParameter("period", 14))
            .DefaultIfEmpty(0)
            .Max();

        var warmUpCount = Math.Min(maxPeriod + 10, klinesResult.Value.Count);
        for (var i = 0; i < warmUpCount; i++)
            tradingStrategy.WarmUpPrice(klinesResult.Value[i].Close);

        // Sincronizar estado previo de indicadores para evitar señales falsas
        if (tradingStrategy is Strategies.DefaultTradingStrategy dts)
            dts.SyncPreviousIndicatorState();

        // 5. Ejecutar backtest con las velas restantes (después del warm-up)
        var backtestKlines = klinesResult.Value.Skip(warmUpCount).ToList();
        if (backtestKlines.Count == 0)
            return Result<BacktestResult, DomainError>.Failure(
                DomainError.Validation("No hay suficientes datos después del warm-up de indicadores."));

        var ruleEngine = serviceProvider.GetRequiredService<IRuleEngine>();
        var engine = serviceProvider.GetRequiredService<BacktestEngine>();

        var result = await engine.RunAsync(
            strategy, tradingStrategy, ruleEngine, backtestKlines, cancellationToken);

        return Result<BacktestResult, DomainError>.Success(result);
    }
}
