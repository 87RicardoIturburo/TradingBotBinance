using MediatR;
using Microsoft.AspNetCore.Mvc;
using TradingBot.API.Dtos;
using TradingBot.API.Middleware;
using TradingBot.Application.Commands.Strategies;
using TradingBot.Application.Queries.Strategies;

namespace TradingBot.API.Controllers;

[ApiController]
[Route("api/[controller]")]
public sealed class StrategiesController(ISender mediator) : ControllerBase
{
    /// <summary>Lista todas las estrategias.</summary>
    [HttpGet]
    public async Task<IResult> GetAll(CancellationToken ct)
    {
        var strategies = await mediator.Send(new GetAllStrategiesQuery(), ct);
        return Results.Ok(strategies.Select(StrategyDto.FromDomain));
    }

    /// <summary>Obtiene una estrategia por Id.</summary>
    [HttpGet("{id:guid}")]
    public async Task<IResult> GetById(Guid id, CancellationToken ct)
    {
        var result = await mediator.Send(new GetStrategyByIdQuery(id), ct);
        return result.ToHttpResult(StrategyDto.FromDomain);
    }

    /// <summary>Crea una nueva estrategia.</summary>
    [HttpPost]
    public async Task<IResult> Create([FromBody] CreateStrategyRequest request, CancellationToken ct)
    {
        var result = await mediator.Send(new CreateStrategyCommand(
            request.Name, request.Symbol, request.Mode,
            request.MaxOrderAmountUsdt, request.MaxDailyLossUsdt,
            request.StopLossPercent, request.TakeProfitPercent,
            request.MaxOpenPositions, request.Description), ct);

        return result.ToHttpResult(StrategyDto.FromDomain, StatusCodes.Status201Created);
    }

    /// <summary>Actualiza una estrategia existente (hot-reload si activa).</summary>
    [HttpPut("{id:guid}")]
    public async Task<IResult> Update(Guid id, [FromBody] UpdateStrategyRequest request, CancellationToken ct)
    {
        var result = await mediator.Send(new UpdateStrategyCommand(
            id, request.Name,
            request.MaxOrderAmountUsdt, request.MaxDailyLossUsdt,
            request.StopLossPercent, request.TakeProfitPercent,
            request.MaxOpenPositions, request.Description), ct);

        return result.ToHttpResult(StrategyDto.FromDomain);
    }

    /// <summary>Elimina una estrategia inactiva.</summary>
    [HttpDelete("{id:guid}")]
    public async Task<IResult> Delete(Guid id, CancellationToken ct)
    {
        var result = await mediator.Send(new DeleteStrategyCommand(id), ct);
        return result.ToHttpResult();
    }

    /// <summary>Activa una estrategia.</summary>
    [HttpPost("{id:guid}/activate")]
    public async Task<IResult> Activate(Guid id, CancellationToken ct)
    {
        var result = await mediator.Send(new ActivateStrategyCommand(id), ct);
        return result.ToHttpResult(StrategyDto.FromDomain);
    }

    /// <summary>Desactiva una estrategia.</summary>
    [HttpPost("{id:guid}/deactivate")]
    public async Task<IResult> Deactivate(Guid id, CancellationToken ct)
    {
        var result = await mediator.Send(new DeactivateStrategyCommand(id), ct);
        return result.ToHttpResult(StrategyDto.FromDomain);
    }
}

// ── Request DTOs ──────────────────────────────────────────────────────────

public sealed record CreateStrategyRequest(
    string                   Name,
    string                   Symbol,
    Core.Enums.TradingMode   Mode,
    decimal                  MaxOrderAmountUsdt,
    decimal                  MaxDailyLossUsdt,
    decimal                  StopLossPercent,
    decimal                  TakeProfitPercent,
    int                      MaxOpenPositions,
    string?                  Description = null);

public sealed record UpdateStrategyRequest(
    string  Name,
    decimal MaxOrderAmountUsdt,
    decimal MaxDailyLossUsdt,
    decimal StopLossPercent,
    decimal TakeProfitPercent,
    int     MaxOpenPositions,
    string? Description = null);
