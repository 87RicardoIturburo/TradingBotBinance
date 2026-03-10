using MediatR;
using Microsoft.AspNetCore.Mvc;
using TradingBot.API.Dtos;
using TradingBot.API.Middleware;
using TradingBot.Application.Backtesting;

namespace TradingBot.API.Controllers;

[ApiController]
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
        var command = new RunBacktestCommand(request.StrategyId, request.From, request.To);
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

        var command = new RunOptimizationCommand(
            request.StrategyId, request.From, request.To, ranges);

        var result = await mediator.Send(command, ct);
        return result.ToHttpResult(OptimizationResultDto.FromDomain);
    }
}
