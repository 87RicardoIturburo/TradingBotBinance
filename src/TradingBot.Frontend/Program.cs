using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using TradingBot.Frontend;
using TradingBot.Frontend.Services;

var builder = WebAssemblyHostBuilder.CreateDefault(args);
builder.RootComponents.Add<App>("#app");
builder.RootComponents.Add<HeadOutlet>("head::after");

// URL de la API — configurable vía appsettings.json del frontend
var apiBaseUrl = builder.Configuration["ApiBaseUrl"] ?? "https://localhost:7114";
var apiKey     = builder.Configuration["ApiKey"] ?? string.Empty;

// Handler que inyecta X-Api-Key en todas las solicitudes
builder.Services.AddTransient(_ => new ApiKeyDelegatingHandler(apiKey));

builder.Services.AddHttpClient<TradingApiClient>(client =>
{
    client.BaseAddress = new Uri(apiBaseUrl);
}).AddHttpMessageHandler<ApiKeyDelegatingHandler>();

// HttpClient genérico para uso en componentes que lo inyecten directamente
builder.Services.AddScoped(sp =>
{
    var handler = sp.GetRequiredService<ApiKeyDelegatingHandler>();
    handler.InnerHandler = new HttpClientHandler();
    return new HttpClient(handler) { BaseAddress = new Uri(apiBaseUrl) };
});

await builder.Build().RunAsync();
