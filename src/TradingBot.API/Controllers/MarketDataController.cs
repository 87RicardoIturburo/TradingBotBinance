using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using TradingBot.Core.Enums;
using TradingBot.Core.Interfaces.Services;
using TradingBot.Core.ValueObjects;

namespace TradingBot.API.Controllers;

[ApiController]
[Authorize]
[Route("api/[controller]")]
public sealed class MarketDataController(IMarketDataService marketData) : ControllerBase
{
    [HttpGet("klines")]
    public async Task<IResult> GetKlines(
        [FromQuery] string symbol,
        [FromQuery] string timeframe = "15m",
        [FromQuery] int limit = 200,
        CancellationToken ct = default)
    {
        var symbolResult = Symbol.Create(symbol);
        if (symbolResult.IsFailure)
            return Results.BadRequest(symbolResult.Error.Message);

        if (!Enum.TryParse<CandleInterval>(timeframe, true, out var interval))
            interval = CandleInterval.FifteenMinutes;

        var to = DateTimeOffset.UtcNow;
        var minutes = interval.ToMinutes() * limit;
        var from = to.AddMinutes(-minutes);

        var result = await marketData.GetKlinesAsync(symbolResult.Value, from, to, interval, ct);
        if (result.IsFailure)
            return Results.Problem(result.Error.Message);

        return Results.Ok(result.Value.Select(k => new
        {
            k.OpenTime,
            k.Open,
            k.High,
            k.Low,
            k.Close,
            k.Volume
        }));
    }
}
