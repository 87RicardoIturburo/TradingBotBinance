using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using TradingBot.Core.Enums;
using TradingBot.Core.Interfaces.Services;

namespace TradingBot.API.Controllers;

[ApiController]
[Authorize]
[Route("api/[controller]")]
public sealed class AutoPilotController(
    IStrategyRotator rotator,
    IStrategyEngine engine) : ControllerBase
{
    /// <summary>
    /// Fuerza una evaluación de rotación para un symbol dado.
    /// Útil para testing o para disparar rotación manual.
    /// </summary>
    [HttpPost("evaluate")]
    public async Task<IResult> EvaluateRotation(
        [FromBody] EvaluateRotationRequest request,
        CancellationToken ct)
    {
        var regime = Enum.TryParse<MarketRegime>(request.Regime, true, out var parsed)
            ? parsed : MarketRegime.Unknown;

        var result = await rotator.EvaluateRotationAsync(
            request.Symbol, regime, request.IsBullish, ct);

        if (result.IsFailure)
            return Results.Problem(result.Error.Message);

        return Results.Ok(result.Value);
    }

    /// <summary>
    /// Obtiene el estado actual de régimen por symbol desde el StrategyEngine.
    /// </summary>
    [HttpGet("status")]
    public async Task<IResult> GetStatus(CancellationToken ct)
    {
        var statuses = await engine.GetStatusAsync(ct);

        var bySymbol = statuses.Values
            .GroupBy(s => s.Symbol.Value, StringComparer.OrdinalIgnoreCase)
            .Select(g => new
            {
                Symbol = g.Key,
                Regime = g.First().CurrentRegime.ToString(),
                ActiveStrategies = g.Select(s => new
                {
                    s.StrategyId,
                    s.StrategyName,
                    s.IsProcessing,
                    s.SignalsGenerated,
                    s.OrdersPlaced
                }).ToList()
            });

        return Results.Ok(bySymbol);
    }
}

public sealed record EvaluateRotationRequest(
    string Symbol,
    string Regime,
    bool IsBullish = true);
