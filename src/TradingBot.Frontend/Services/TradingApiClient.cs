using System.Net.Http.Json;
using TradingBot.Frontend.Models;

namespace TradingBot.Frontend.Services;

/// <summary>
/// Cliente HTTP tipado para comunicarse con la API de TradingBot.
/// </summary>
public sealed class TradingApiClient(HttpClient http)
{
    // ── Strategies ────────────────────────────────────────────────────────

    public Task<List<StrategyDto>?> GetStrategiesAsync() =>
        http.GetFromJsonAsync<List<StrategyDto>>("api/strategies");

    public Task<StrategyDto?> GetStrategyAsync(Guid id) =>
        http.GetFromJsonAsync<StrategyDto>($"api/strategies/{id}");

    public async Task<StrategyDto?> CreateStrategyAsync(CreateStrategyRequest request)
    {
        var response = await http.PostAsJsonAsync("api/strategies", request);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<StrategyDto>();
    }

    public async Task<bool> DeleteStrategyAsync(Guid id)
    {
        var response = await http.DeleteAsync($"api/strategies/{id}");
        return response.IsSuccessStatusCode;
    }

    public async Task<StrategyDto?> ActivateStrategyAsync(Guid id)
    {
        var response = await http.PostAsync($"api/strategies/{id}/activate", null);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<StrategyDto>();
    }

    public async Task<StrategyDto?> DeactivateStrategyAsync(Guid id)
    {
        var response = await http.PostAsync($"api/strategies/{id}/deactivate", null);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<StrategyDto>();
    }

    // ── Orders ────────────────────────────────────────────────────────────

    public Task<List<OrderDto>?> GetOrdersAsync(DateTimeOffset? from = null, DateTimeOffset? to = null)
    {
        var query = "api/orders";
        if (from.HasValue || to.HasValue)
        {
            var parts = new List<string>();
            if (from.HasValue) parts.Add($"from={from.Value:O}");
            if (to.HasValue) parts.Add($"to={to.Value:O}");
            query += "?" + string.Join("&", parts);
        }
        return http.GetFromJsonAsync<List<OrderDto>>(query);
    }

    public Task<List<OrderDto>?> GetOpenOrdersAsync() =>
        http.GetFromJsonAsync<List<OrderDto>>("api/orders/open");

    public async Task<bool> CancelOrderAsync(Guid id)
    {
        var response = await http.DeleteAsync($"api/orders/{id}");
        return response.IsSuccessStatusCode;
    }

    // ── System ────────────────────────────────────────────────────────────

    public Task<SystemStatusDto?> GetSystemStatusAsync() =>
        http.GetFromJsonAsync<SystemStatusDto>("api/system/status");

    public Task PauseAsync() =>
        http.PostAsync("api/system/pause", null);

    public Task ResumeAsync() =>
        http.PostAsync("api/system/resume", null);
}
