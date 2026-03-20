using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using TradingBot.API.Dtos;
using TradingBot.API.Middleware;
using TradingBot.Application.Commands.Orders;
using TradingBot.Application.Explainer;
using TradingBot.Application.Queries.Orders;

namespace TradingBot.API.Controllers;

[ApiController]
[Authorize]
[Route("api/[controller]")]
public sealed class OrdersController(ISender mediator) : ControllerBase
{
    /// <summary>Obtiene las órdenes abiertas.</summary>
    [HttpGet("open")]
    public async Task<IResult> GetOpen(CancellationToken ct)
    {
        var result = await mediator.Send(new GetOpenOrdersQuery(), ct);
        return result.ToHttpResult(
            orders => (object)orders.Select(OrderDto.FromDomain).ToList());
    }

    /// <summary>Obtiene órdenes en un rango de fechas.</summary>
    [HttpGet]
    public async Task<IResult> GetByDateRange(
        [FromQuery] DateTimeOffset? from,
        [FromQuery] DateTimeOffset? to,
        CancellationToken ct)
    {
        var fromDate = from ?? DateTimeOffset.UtcNow.AddDays(-7);
        var toDate   = to   ?? DateTimeOffset.UtcNow;

        var orders = await mediator.Send(
            new GetOrdersByDateRangeQuery(fromDate, toDate), ct);

        return Results.Ok(orders.Select(OrderDto.FromDomain));
    }

    /// <summary>
    /// Obtiene el historial de trades ejecutados con explicación de por qué se tomó cada decisión.
    /// </summary>
    [HttpGet("history")]
    public async Task<IResult> GetTradeHistory(
        [FromQuery] Guid? strategyId,
        [FromQuery] DateTimeOffset? from,
        [FromQuery] DateTimeOffset? to,
        [FromQuery] int limit = 50,
        CancellationToken ct = default)
    {
        var fromDate = from ?? DateTimeOffset.UtcNow.AddDays(-7);
        var toDate   = to   ?? DateTimeOffset.UtcNow;

        var orders = await mediator.Send(
            new GetTradeHistoryQuery(strategyId, fromDate, toDate, limit), ct);

        return Results.Ok(orders.Select(OrderWithExplanationDto.FromDomain));
    }

    /// <summary>Cancela una orden activa.</summary>
    [HttpDelete("{id:guid}")]
    public async Task<IResult> Cancel(Guid id, CancellationToken ct)
    {
        var result = await mediator.Send(new CancelOrderCommand(id), ct);
        return result.ToHttpResult(OrderDto.FromDomain);
    }
}
