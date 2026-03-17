using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using TradingBot.Frontend;
using TradingBot.Frontend.Services;

var builder = WebAssemblyHostBuilder.CreateDefault(args);
builder.RootComponents.Add<App>("#app");
builder.RootComponents.Add<HeadOutlet>("head::after");

// URL de la API — configurable vía appsettings.json del frontend
var apiBaseUrl = builder.Configuration["ApiBaseUrl"] ?? "https://localhost:7114";

// Handler que envía la cookie de sesión HttpOnly en cada request (BFF pattern)
builder.Services.AddTransient<CookieDelegatingHandler>();

builder.Services.AddHttpClient<TradingApiClient>(client =>
{
    client.BaseAddress = new Uri(apiBaseUrl);
}).AddHttpMessageHandler<CookieDelegatingHandler>();

// HttpClient genérico para uso en componentes que lo inyecten directamente
builder.Services.AddScoped(sp =>
{
    var handler = sp.GetRequiredService<CookieDelegatingHandler>();
    handler.InnerHandler = new HttpClientHandler();
    return new HttpClient(handler) { BaseAddress = new Uri(apiBaseUrl) };
});

// Servicio de estado de autenticación
builder.Services.AddScoped<AuthStateService>();

await builder.Build().RunAsync();
