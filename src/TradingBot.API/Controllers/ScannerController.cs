using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using TradingBot.API.Middleware;
using TradingBot.Application.Scanner;

namespace TradingBot.API.Controllers;

[ApiController]
[Authorize]
[Route("api/[controller]")]
public sealed class ScannerController(ISender mediator) : ControllerBase
{
    /// <summary>
    /// Escanea el mercado y devuelve los mejores símbolos para operar,
    /// ordenados por Tradability Score.
    /// </summary>
    [HttpGet]
    public async Task<IResult> GetTopSymbols(
        [FromQuery] int top = 50,
        CancellationToken ct = default)
    {
        var result = await mediator.Send(new GetTopSymbolsQuery(top), ct);
        return result.ToHttpResult();
    }
}
