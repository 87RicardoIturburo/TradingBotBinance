using Microsoft.AspNetCore.Mvc;
using TradingBot.API.Dtos;
using TradingBot.Core.Interfaces.Services;

namespace TradingBot.API.Controllers;

[ApiController]
[Route("api/[controller]")]
public sealed class SystemController(
    IStrategyEngine    strategyEngine,
    IMarketDataService marketDataService,
    ICacheService      cacheService) : ControllerBase
{
    /// <summary>Devuelve el estado del bot y de todas las estrategias activas.</summary>
    [HttpGet("status")]
    public async Task<IResult> GetStatus(CancellationToken ct)
    {
        var strategies = await strategyEngine.GetStatusAsync(ct);
        var mapped = strategies.ToDictionary(
            kv => kv.Key,
            kv => StrategyEngineStatusDto.FromDomain(kv.Value));

        var dto = new SystemStatusDto(
            strategyEngine.IsRunning,
            marketDataService.IsConnected,
            mapped);

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

    /// <summary>Obtiene los pares de trading disponibles en Binance (cacheado 1h en Redis).</summary>
    [HttpGet("symbols")]
    public async Task<IResult> GetSymbols(
        [FromQuery] string quoteAsset = "USDT",
        CancellationToken ct = default)
    {
        var cacheKey = $"symbols:{quoteAsset.ToUpperInvariant()}";

        var cached = await cacheService.GetAsync<List<SymbolInfoDto>>(cacheKey, ct);
        if (cached is not null)
            return Results.Ok(cached);

        var result = await marketDataService.GetTradingSymbolsAsync(quoteAsset, ct);
        if (result.IsFailure)
            return Results.Problem(result.Error.Message, statusCode: 502);

        var dtos = result.Value
            .Select(s => new SymbolInfoDto(s.Symbol, s.BaseAsset, s.QuoteAsset))
            .ToList();

        await cacheService.SetAsync(cacheKey, dtos, TimeSpan.FromHours(1), ct);

        return Results.Ok(dtos);
    }
}
