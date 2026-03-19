using System.Net.Http.Json;
using System.Text.Json;
using TradingBot.Frontend.Models;

namespace TradingBot.Frontend.Services;

/// <summary>
/// Cliente HTTP tipado para comunicarse con la API de TradingBot.
/// </summary>
public sealed class TradingApiClient(HttpClient http)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    // ── Strategies ────────────────────────────────────────────────────────

    public Task<List<StrategyDto>?> GetStrategiesAsync() =>
        http.GetFromJsonAsync<List<StrategyDto>>("api/strategies");

    public Task<StrategyDto?> GetStrategyAsync(Guid id) =>
        http.GetFromJsonAsync<StrategyDto>($"api/strategies/{id}");

    public async Task<StrategyDto?> CreateStrategyAsync(CreateStrategyRequest request)
    {
        var response = await http.PostAsJsonAsync("api/strategies", request);
        await EnsureSuccessAsync(response);
        return await response.Content.ReadFromJsonAsync<StrategyDto>();
    }

    public async Task<bool> DeleteStrategyAsync(Guid id)
    {
        var response = await http.DeleteAsync($"api/strategies/{id}");
        return response.IsSuccessStatusCode;
    }

    public async Task<StrategyDto?> UpdateStrategyAsync(Guid id, UpdateStrategyRequest request)
    {
        var response = await http.PutAsJsonAsync($"api/strategies/{id}", request);
        await EnsureSuccessAsync(response);
        return await response.Content.ReadFromJsonAsync<StrategyDto>();
    }

    public async Task<StrategyDto?> DuplicateStrategyAsync(Guid id)
    {
        var response = await http.PostAsync($"api/strategies/{id}/duplicate", null);
        await EnsureSuccessAsync(response);
        return await response.Content.ReadFromJsonAsync<StrategyDto>();
    }

    public async Task<StrategyDto?> ActivateStrategyAsync(Guid id)
    {
        var response = await http.PostAsync($"api/strategies/{id}/activate", null);
        await EnsureSuccessAsync(response);
        return await response.Content.ReadFromJsonAsync<StrategyDto>();
    }

    public async Task<StrategyDto?> DeactivateStrategyAsync(Guid id)
    {
        var response = await http.PostAsync($"api/strategies/{id}/deactivate", null);
        await EnsureSuccessAsync(response);
        return await response.Content.ReadFromJsonAsync<StrategyDto>();
    }

    public async Task<StrategyDto?> AddIndicatorAsync(Guid strategyId, AddIndicatorRequest request)
    {
        var response = await http.PostAsJsonAsync($"api/strategies/{strategyId}/indicators", request);
        await EnsureSuccessAsync(response);
        return await response.Content.ReadFromJsonAsync<StrategyDto>();
    }

    public async Task<StrategyDto?> UpdateIndicatorAsync(Guid strategyId, AddIndicatorRequest request)
    {
        var response = await http.PutAsJsonAsync($"api/strategies/{strategyId}/indicators", request);
        await EnsureSuccessAsync(response);
        return await response.Content.ReadFromJsonAsync<StrategyDto>();
    }

    public async Task<bool> RemoveIndicatorAsync(Guid strategyId, string type)
    {
        var response = await http.DeleteAsync($"api/strategies/{strategyId}/indicators/{type}");
        return response.IsSuccessStatusCode;
    }

    public async Task<StrategyDto?> AddRuleAsync(Guid strategyId, AddRuleRequest request)
    {
        var response = await http.PostAsJsonAsync($"api/strategies/{strategyId}/rules", request);
        await EnsureSuccessAsync(response);
        return await response.Content.ReadFromJsonAsync<StrategyDto>();
    }

    public async Task<StrategyDto?> UpdateRuleAsync(Guid strategyId, Guid ruleId, UpdateRuleRequest request)
    {
        var response = await http.PutAsJsonAsync($"api/strategies/{strategyId}/rules/{ruleId}", request);
        await EnsureSuccessAsync(response);
        return await response.Content.ReadFromJsonAsync<StrategyDto>();
    }

    public async Task<bool> RemoveRuleAsync(Guid strategyId, Guid ruleId)
    {
        var response = await http.DeleteAsync($"api/strategies/{strategyId}/rules/{ruleId}");
        return response.IsSuccessStatusCode;
    }

    public async Task SaveOptimizationProfileAsync(Guid strategyId, List<SavedParameterRangeDto> ranges)
    {
        var response = await http.PutAsJsonAsync(
            $"api/strategies/{strategyId}/optimization-profile",
            new { Ranges = ranges });
        await EnsureSuccessAsync(response);
    }

    // ── Templates ─────────────────────────────────────────────────────────

    public Task<List<StrategyTemplateDto>?> GetTemplatesAsync() =>
        http.GetFromJsonAsync<List<StrategyTemplateDto>>("api/strategies/templates");

    // ── Orders ────────────────────────────────────────────────────────────

    public Task<List<OrderDto>?> GetOrdersAsync(DateTimeOffset? from = null, DateTimeOffset? to = null)
    {
        var query = "api/orders";
        if (from.HasValue || to.HasValue)
        {
            var parts = new List<string>();
            if (from.HasValue) parts.Add($"from={Uri.EscapeDataString(from.Value.ToString("O"))}");
            if (to.HasValue) parts.Add($"to={Uri.EscapeDataString(to.Value.ToString("O"))}");
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

    public Task<List<SymbolInfoDto>?> GetSymbolsAsync(string quoteAsset = "USDT") =>
        http.GetFromJsonAsync<List<SymbolInfoDto>>($"api/system/symbols?quoteAsset={quoteAsset}");

    public Task<List<AccountBalanceDto>?> GetBalanceAsync() =>
        http.GetFromJsonAsync<List<AccountBalanceDto>>("api/system/balance");

    public Task<PortfolioExposureDto?> GetPortfolioExposureAsync() =>
        http.GetFromJsonAsync<PortfolioExposureDto>("api/system/exposure");

    public Task<MetricsSnapshotDto?> GetMetricsAsync() =>
        http.GetFromJsonAsync<MetricsSnapshotDto>("api/system/metrics");

    public async Task<InfrastructureHealthDto?> GetHealthAsync()
    {
        try
        {
            return await http.GetFromJsonAsync<InfrastructureHealthDto>("api/system/health");
        }
        catch
        {
            return null;
        }
    }

    // ── Positions & P&L ──────────────────────────────────────────────────

    public Task<List<PositionDto>?> GetOpenPositionsAsync() =>
        http.GetFromJsonAsync<List<PositionDto>>("api/positions/open");

    public Task<List<PositionDto>?> GetClosedPositionsAsync(
        DateTimeOffset? from = null, DateTimeOffset? to = null)
    {
        var query = "api/positions/closed";
        var parts = new List<string>();
        if (from.HasValue) parts.Add($"from={Uri.EscapeDataString(from.Value.ToString("O"))}");
        if (to.HasValue) parts.Add($"to={Uri.EscapeDataString(to.Value.ToString("O"))}");
        if (parts.Count > 0) query += "?" + string.Join("&", parts);
        return http.GetFromJsonAsync<List<PositionDto>>(query);
    }

    public Task<List<PnLSummaryDto>?> GetPnLSummaryAsync() =>
        http.GetFromJsonAsync<List<PnLSummaryDto>>("api/positions/summary");

    // ── Backtest ─────────────────────────────────────────────────────────

    public async Task<BacktestResultDto> RunBacktestAsync(RunBacktestRequest request)
    {
        var response = await http.PostAsJsonAsync("api/backtest", request);
        await EnsureSuccessAsync(response);
        return (await response.Content.ReadFromJsonAsync<BacktestResultDto>())!;
    }

    public async Task<OptimizationResultDto> RunOptimizationAsync(RunOptimizationRequest request)
    {
        var response = await http.PostAsJsonAsync("api/backtest/optimize", request);
        await EnsureSuccessAsync(response);
        return (await response.Content.ReadFromJsonAsync<OptimizationResultDto>())!;
    }

    // ── Auth (BFF) ──────────────────────────────────────────────────────

    public async Task<bool> LoginAsync(string apiKey)
    {
        var response = await http.PostAsJsonAsync("api/auth/login", new { apiKey });
        return response.IsSuccessStatusCode;
    }

    public async Task LogoutAsync()
    {
        await http.PostAsync("api/auth/logout", null);
    }

    public async Task<bool> GetAuthStatusAsync()
    {
        try
        {
            var response = await http.GetFromJsonAsync<AuthStatusResponse>("api/auth/status");
            return response?.Authenticated == true;
        }
        catch
        {
            return false;
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────

    /// <summary>
    /// Lee el cuerpo de la respuesta de error de la API y lanza una excepción
    /// con el mensaje real del servidor en lugar del genérico de HTTP.
    /// </summary>
    private static async Task EnsureSuccessAsync(HttpResponseMessage response)
    {
        if (response.IsSuccessStatusCode)
            return;

        var message = await ExtractErrorMessageAsync(response);
        throw new HttpRequestException(message, inner: null, response.StatusCode);
    }

    private static async Task<string> ExtractErrorMessageAsync(HttpResponseMessage response)
    {
        try
        {
            var body = await response.Content.ReadAsStringAsync();
            if (!string.IsNullOrWhiteSpace(body))
            {
                using var doc = JsonDocument.Parse(body);
                if (doc.RootElement.TryGetProperty("error", out var errorProp))
                    return errorProp.GetString() ?? body;
            }
        }
        catch (JsonException)
        {
            // El cuerpo no es JSON válido; se usa el mensaje genérico.
        }

        return $"Error {(int)response.StatusCode}: {response.ReasonPhrase}";
    }
}
