using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using TradingBot.Core.Interfaces.Services;

namespace TradingBot.API.Controllers;

[ApiController]
[Authorize]
[Route("api/[controller]")]
public sealed class SymbolPoolController(ISymbolPool pool) : ControllerBase
{
    [HttpGet("status")]
    public async Task<IResult> GetStatus(CancellationToken ct)
    {
        var enabled = await pool.IsEnabledAsync(ct);
        var scores = await pool.GetTradabilityScoresAsync(ct);
        var active = scores.Count(s => s.IsActive);
        var blocked = scores.Count(s => !s.IsActive);

        return Results.Ok(new { Enabled = enabled, Evaluated = scores.Count, Active = active, Blocked = blocked });
    }

    [HttpGet("scores")]
    public async Task<IResult> GetScores(CancellationToken ct)
    {
        var scores = await pool.GetTradabilityScoresAsync(ct);
        return Results.Ok(scores);
    }

    [HttpGet("active")]
    public async Task<IResult> GetActive(CancellationToken ct)
    {
        var scores = await pool.GetTradabilityScoresAsync(ct);
        return Results.Ok(scores.Where(s => s.IsActive));
    }

    [HttpPost("enable")]
    public async Task<IResult> Enable(CancellationToken ct)
    {
        await pool.SetEnabledAsync(true, ct);
        return Results.Ok(new { Enabled = true });
    }

    [HttpPost("disable")]
    public async Task<IResult> Disable(CancellationToken ct)
    {
        await pool.SetEnabledAsync(false, ct);
        return Results.Ok(new { Enabled = false });
    }

    [HttpPost("force-refresh")]
    public async Task<IResult> ForceRefresh(CancellationToken ct)
    {
        await pool.ForceRefreshAsync(ct);
        return Results.Ok(new { Message = "Refresh forzado iniciado" });
    }

    [HttpPost("exclude")]
    public async Task<IResult> ExcludeSymbol([FromBody] ExcludeSymbolRequest request, CancellationToken ct)
    {
        await pool.ExcludeSymbolAsync(request.Symbol, TimeSpan.FromMinutes(30), ct);
        return Results.Ok(new { Symbol = request.Symbol, ExcludedMinutes = 30 });
    }
}

public sealed record ExcludeSymbolRequest(string Symbol);
