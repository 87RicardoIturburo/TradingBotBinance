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
/// Ejecuta un backtest de una estrategia existente contra datos históricos de Binance.
/// Los datos se descargan en memoria y no se persisten.
/// </summary>
public sealed record RunBacktestCommand(
    Guid           StrategyId,
    DateTimeOffset From,
    DateTimeOffset To,
    CandleInterval Interval = CandleInterval.OneMinute) : IRequest<Result<BacktestResult, DomainError>>;

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
            "Descargando klines para backtest: {Symbol} ({From} → {To}) intervalo={Interval}",
            strategy.Symbol.Value, request.From, request.To, request.Interval);

        var klinesResult = await marketDataService.GetKlinesAsync(
            strategy.Symbol, request.From, request.To, request.Interval, cancellationToken);

        if (klinesResult.IsFailure)
            return Result<BacktestResult, DomainError>.Failure(klinesResult.Error);

        if (klinesResult.Value.Count == 0)
            return Result<BacktestResult, DomainError>.Failure(
                DomainError.Validation("No se encontraron datos históricos para el rango especificado."));

        // Validar calidad de datos: alertar si hay saltos de precio extremos entre velas.
        // Un salto > 20% entre velas consecutivas suele indicar datos sintéticos (ej: Binance Demo)
        // o un timeframe incorrecto, y arruina los resultados del backtest.
        var klines = klinesResult.Value;
        var maxJump = 0m;
        for (var idx = 1; idx < Math.Min(klines.Count, 200); idx++)
        {
            var prev = klines[idx - 1].Close;
            var curr = klines[idx].Close;
            if (prev > 0)
                maxJump = Math.Max(maxJump, Math.Abs(curr - prev) / prev * 100m);
        }
        logger.LogInformation(
            "Calidad de datos: {Count} velas | Precio inicial={First:F2} | Precio final={Last:F2} | "
            + "Salto máximo entre velas (muestra 200)={MaxJump:F2}%",
            klines.Count, klines[0].Close, klines[^1].Close, maxJump);
        if (maxJump > 20m)
            logger.LogWarning(
                "⚠ CALIDAD DE DATOS: salto de {MaxJump:F2}% entre velas consecutivas. "
                + "Los datos del entorno Demo pueden ser sintéticos e irreales. "
                + "Probá con un período diferente o usá Testnet en lugar de Demo.",
                maxJump);

        // 3. Crear instancia fresca de ITradingStrategy para el backtest
        var tradingStrategy = serviceProvider.GetRequiredService<ITradingStrategy>();
        await tradingStrategy.InitializeAsync(strategy, cancellationToken);

        // 4. Pre-calentar indicadores SIN evaluar señales (evita contaminar
        //    _lastSignalAt y _previousRsi con señales fantasma del warm-up)
        var maxPeriod = strategy.Indicators
            .Select(i => (int)i.GetParameter("period", 14))
            .DefaultIfEmpty(0)
            .Max();

        var warmUpCount = Math.Min(maxPeriod + 10, klines.Count);
        for (var i = 0; i < warmUpCount; i++)
            tradingStrategy.WarmUpOhlc(klines[i].High, klines[i].Low, klines[i].Close, klines[i].Volume);

        // Sincronizar estado previo de indicadores para evitar señales falsas
        if (tradingStrategy is Strategies.DefaultTradingStrategy dts)
            dts.SyncPreviousIndicatorState();

        // 5. Ejecutar backtest con las velas restantes (después del warm-up)
        var backtestKlines = klines.Skip(warmUpCount).ToList();
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
