using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using TradingBot.Frontend;
using TradingBot.Frontend.Services;

var builder = WebAssemblyHostBuilder.CreateDefault(args);
builder.RootComponents.Add<App>("#app");
builder.RootComponents.Add<HeadOutlet>("head::after");

// URL de la API — configurable vía appsettings.json del frontend
var apiBaseUrl = builder.Configuration["ApiBaseUrl"] ?? "https://localhost:7114";

builder.Services.AddHttpClient<TradingApiClient>(client =>
{
    client.BaseAddress = new Uri(apiBaseUrl);
});

// HttpClient genérico para uso en componentes que lo inyecten directamente
builder.Services.AddScoped(sp => new HttpClient { BaseAddress = new Uri(apiBaseUrl) });

await builder.Build().RunAsync();
