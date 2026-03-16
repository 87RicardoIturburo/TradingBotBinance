using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using TradingBot.API.Dtos;
using TradingBot.Application.Queries.Positions;

namespace TradingBot.API.Controllers;

[ApiController]
[Authorize]
[Route("api/[controller]")]
public sealed class PositionsController(ISender mediator) : ControllerBase
{
    /// <summary>Obtiene todas las posiciones abiertas.</summary>
    [HttpGet("open")]
    public async Task<IResult> GetOpen(CancellationToken ct)
    {
        var positions = await mediator.Send(new GetOpenPositionsQuery(), ct);
        return Results.Ok(positions.Select(PositionDto.FromDomain));
    }

    /// <summary>Obtiene posiciones cerradas en un rango de fechas.</summary>
    [HttpGet("closed")]
    public async Task<IResult> GetClosed(
        [FromQuery] DateTimeOffset? from,
        [FromQuery] DateTimeOffset? to,
        CancellationToken ct)
    {
        var fromDate = from ?? DateTimeOffset.UtcNow.AddDays(-30);
        var toDate   = to   ?? DateTimeOffset.UtcNow;

        var positions = await mediator.Send(
            new GetClosedPositionsQuery(fromDate, toDate), ct);

        return Results.Ok(positions.Select(PositionDto.FromDomain));
    }

    /// <summary>Resumen de P&amp;L por estrategia.</summary>
    [HttpGet("summary")]
    public async Task<IResult> GetSummary(CancellationToken ct)
    {
        var summary = await mediator.Send(new GetPnLSummaryQuery(), ct);
        return Results.Ok(summary.Select(PnLSummaryDto.FromDomain));
    }
}
