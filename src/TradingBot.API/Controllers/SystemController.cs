using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using TradingBot.API.Dtos;
using TradingBot.Application.Diagnostics;
using TradingBot.Core.Interfaces.Services;

namespace TradingBot.API.Controllers;

[ApiController]
[Authorize]
[Route("api/[controller]")]
public sealed class SystemController(
    IStrategyEngine       strategyEngine,
    IMarketDataService    marketDataService,
    ICacheService         cacheService,
    IAccountService       accountService,
    IRiskManager          riskManager,
    IGlobalCircuitBreaker circuitBreaker,
    TradingMetrics        tradingMetrics) : ControllerBase
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

    /// <summary>Devuelve el balance de la cuenta (solo assets con saldo &gt; 0).</summary>
    [HttpGet("balance")]
    public async Task<IResult> GetBalance(CancellationToken ct)
    {
        var result = await accountService.GetAccountSnapshotAsync(ct);
        if (result.IsFailure)
            return Results.Problem(result.Error.Message, statusCode: 502);

        var dtos = result.Value
            .Select(b => new AccountBalanceDto(b.Asset, b.Free, b.Locked, b.Total))
            .ToList();

        return Results.Ok(dtos);
    }

    /// <summary>Devuelve la exposición del portafolio por símbolo (long/short).</summary>
    [HttpGet("exposure")]
    public async Task<IResult> GetExposure(CancellationToken ct)
    {
        var (totalLong, totalShort, net) = await riskManager.GetPortfolioExposureAsync(ct);
        var bySymbol = await riskManager.GetExposureBySymbolAsync(ct);
        var (isDrawdown, drawdownPct) = await riskManager.CheckAccountDrawdownAsync(ct);

        var symbolDtos = bySymbol
            .Select(s => new SymbolExposureDto(s.Symbol, s.LongUsdt, s.ShortUsdt, s.NetUsdt))
            .OrderByDescending(s => s.LongUsdt + s.ShortUsdt)
            .ToList();

        return Results.Ok(new PortfolioExposureDto(
            totalLong, totalShort, net, symbolDtos, isDrawdown, drawdownPct));
    }

    /// <summary>Devuelve el estado del circuit breaker global.</summary>
    [HttpGet("circuit-breaker")]
    public IResult GetCircuitBreakerStatus()
    {
        return Results.Ok(new
        {
            circuitBreaker.IsOpen,
            circuitBreaker.TripReason,
            circuitBreaker.TrippedAt
        });
    }

    /// <summary>Resetea el circuit breaker global para reanudar el trading.</summary>
    [HttpPost("circuit-breaker/reset")]
    public IResult ResetCircuitBreaker()
    {
        circuitBreaker.Reset();
        return Results.Ok(new { message = "Circuit breaker reseteado" });
    }

    /// <summary>Devuelve un snapshot de las métricas de trading para el dashboard.</summary>
    [HttpGet("metrics")]
    public IResult GetMetrics()
    {
        var snapshot = tradingMetrics.GetSnapshot();
        return Results.Ok(new MetricsSnapshotDto(
            snapshot.TotalTicksProcessed,
            snapshot.TotalSignalsGenerated,
            snapshot.TotalOrdersPlaced,
            snapshot.TotalOrdersFailed,
            snapshot.TotalTicksDropped,
            snapshot.TotalOrdersPaper,
            snapshot.TotalOrdersLive,
            snapshot.LastLatencyMs,
            snapshot.AverageLatencyMs,
            snapshot.DailyPnLUsdt,
            circuitBreaker.IsOpen,
            circuitBreaker.TripReason,
            snapshot.Timestamp));
    }
}
