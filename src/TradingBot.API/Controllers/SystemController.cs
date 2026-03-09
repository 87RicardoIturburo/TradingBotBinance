using Microsoft.AspNetCore.Mvc;
using TradingBot.API.Dtos;
using TradingBot.Core.Interfaces.Services;

namespace TradingBot.API.Controllers;

[ApiController]
[Route("api/[controller]")]
public sealed class SystemController(
    IStrategyEngine    strategyEngine,
    IMarketDataService marketDataService) : ControllerBase
{
    /// <summary>Devuelve el estado del bot y de todas las estrategias activas.</summary>
    [HttpGet("status")]
    public async Task<IResult> GetStatus(CancellationToken ct)
    {
        var strategies = await strategyEngine.GetStatusAsync(ct);
        var dto = new SystemStatusDto(
            strategyEngine.IsRunning,
            marketDataService.IsConnected,
            strategies);

        return Results.Ok(dto);
    }

    /// <summary>Pausa el motor de estrategias (los WebSockets siguen conectados).</summary>
    [HttpPost("pause")]
    public async Task<IResult> Pause(CancellationToken ct)
    {
        await strategyEngine.PauseAsync(ct);
        return Results.Ok(new { message = "Motor pausado" });
    }

    /// <summary>Reanuda el motor de estrategias.</summary>
    [HttpPost("resume")]
    public async Task<IResult> Resume(CancellationToken ct)
    {
        await strategyEngine.ResumeAsync(ct);
        return Results.Ok(new { message = "Motor reanudado" });
    }
}
