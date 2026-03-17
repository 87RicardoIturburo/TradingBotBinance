using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using TradingBot.API.Dtos;
using TradingBot.API.Middleware;
using TradingBot.Application.Backtesting;
using TradingBot.Core.Enums;

namespace TradingBot.API.Controllers;

[ApiController]
[Authorize]
[Route("api/[controller]")]
public sealed class BacktestController(ISender mediator) : ControllerBase
{
    /// <summary>
    /// Ejecuta un backtest de una estrategia contra datos históricos de Binance.
    /// Los datos se descargan en memoria y no se persisten.
    /// </summary>
    [HttpPost]
    public async Task<IResult> Run(
        [FromBody] RunBacktestRequest request,
        CancellationToken ct)
    {
        var interval = Enum.TryParse<CandleInterval>(request.Interval, true, out var parsedInterval)
            ? parsedInterval
            : CandleInterval.OneMinute;
        var command = new RunBacktestCommand(request.StrategyId, request.From, request.To, interval);
        var result = await mediator.Send(command, ct);
        return result.ToHttpResult(BacktestResultDto.FromDomain);
    }

    /// <summary>
    /// Ejecuta una optimización de parámetros sobre una estrategia.
    /// Prueba todas las combinaciones de parámetros y devuelve los resultados ordenados por P&amp;L.
    /// </summary>
    [HttpPost("optimize")]
    public async Task<IResult> Optimize(
        [FromBody] RunOptimizationRequest request,
        CancellationToken ct)
    {
        var ranges = request.ParameterRanges
            .Select(r => new ParameterRange(r.Name, r.Min, r.Max, r.Step))
            .ToList();

        var rankBy = Enum.TryParse<OptimizationRankBy>(request.RankBy, true, out var parsed)
            ? parsed
            : OptimizationRankBy.PnL;

        var interval = Enum.TryParse<CandleInterval>(request.Interval, true, out var parsedInterval)
            ? parsedInterval
            : CandleInterval.OneMinute;

        var command = new RunOptimizationCommand(
            request.StrategyId, request.From, request.To, ranges, rankBy, interval);

        var result = await mediator.Send(command, ct);
        return result.ToHttpResult(OptimizationResultDto.FromDomain);
    }

    /// <summary>
    /// Walk-forward analysis: optimiza en 70% train, valida en 30% test.
    /// Detecta overfitting si la degradación de métricas supera el 30%.
    /// </summary>
    [HttpPost("walk-forward")]
    public async Task<IResult> WalkForward(
        [FromBody] RunOptimizationRequest request,
        CancellationToken ct)
    {
        var ranges = request.ParameterRanges
            .Select(r => new ParameterRange(r.Name, r.Min, r.Max, r.Step))
            .ToList();

        var rankBy = Enum.TryParse<OptimizationRankBy>(request.RankBy, true, out var parsed)
            ? parsed
            : OptimizationRankBy.SharpeRatio;

        var interval = Enum.TryParse<CandleInterval>(request.Interval, true, out var parsedInterval)
            ? parsedInterval
            : CandleInterval.OneMinute;

        var command = new RunWalkForwardCommand(
            request.StrategyId, request.From, request.To, ranges, rankBy, interval);

        var result = await mediator.Send(command, ct);
        return result.ToHttpResult(WalkForwardResultDto.FromDomain);
    }
}
