using Serilog;
using TradingBot.API.Hubs;
using TradingBot.API.Middleware;
using TradingBot.API.Services;
using TradingBot.Application;
using TradingBot.Core.Interfaces.Services;
using TradingBot.Infrastructure;

// ── Serilog bootstrap ─────────────────────────────────────────────────────
Log.Logger = new LoggerConfiguration()
    .WriteTo.Console()
    .CreateBootstrapLogger();

try
{
    Log.Information("Iniciando TradingBot API…");

    var builder = WebApplication.CreateBuilder(args);

    // Serilog — reemplaza el logging por defecto de ASP.NET Core
    builder.Host.UseSerilog((context, services, loggerConfig) => loggerConfig
        .ReadFrom.Configuration(context.Configuration)
        .ReadFrom.Services(services)
        .Enrich.FromLogContext()
        .Enrich.WithMachineName()
        .Enrich.WithProperty("Application", "TradingBot")
        .WriteTo.Console(
            outputTemplate: "[{Timestamp:HH:mm:ss} {Level:u3}] {SourceContext} | {Message:lj}{NewLine}{Exception}")
        .WriteTo.File(
            path: "logs/tradingbot-.log",
            rollingInterval: RollingInterval.Day,
            retainedFileCountLimit: 30,
            outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {SourceContext} | {Message:lj}{NewLine}{Exception}"));

    // ── Servicios ─────────────────────────────────────────────────────────
    builder.Services.AddApplication(builder.Configuration);
    builder.Services.AddInfrastructure(builder.Configuration);

    builder.Services.AddControllers()
        .AddJsonOptions(opts =>
        {
            opts.JsonSerializerOptions.Converters.Add(
                new System.Text.Json.Serialization.JsonStringEnumConverter());
        });

    // Los controladores devuelven IResult (Results.Ok, etc.), que usa las opciones
    // de JSON de minimal API, no las de MVC. Configuramos ambas para consistencia.
    builder.Services.Configure<Microsoft.AspNetCore.Http.Json.JsonOptions>(opts =>
    {
        opts.SerializerOptions.Converters.Add(
            new System.Text.Json.Serialization.JsonStringEnumConverter());
    });

    builder.Services.AddSignalR();
    builder.Services.AddSingleton<ITradingNotifier, SignalRTradingNotifier>();
    builder.Services.AddOpenApi();

    // CORS — permite al frontend Blazor WASM comunicarse con la API
    builder.Services.AddCors(options =>
    {
        options.AddPolicy("Frontend", policy =>
        {
            policy
                .WithOrigins(
                    builder.Configuration.GetValue<string>("FrontendUrl") ?? "https://localhost:7017",
                    "https://localhost:7017",
                    "http://localhost:5179")
                .AllowAnyHeader()
                .AllowAnyMethod()
                .AllowCredentials(); // Requerido para SignalR
        });
    });

    var app = builder.Build();

    // ── Pipeline HTTP ─────────────────────────────────────────────────────
    app.UseMiddleware<ErrorHandlingMiddleware>();
    app.UseSerilogRequestLogging();

    if (app.Environment.IsDevelopment())
    {
        app.MapOpenApi();
    }

    app.UseHttpsRedirection();
    app.UseCors("Frontend");

    app.MapControllers();
    app.MapHub<TradingHub>("/hubs/trading");

    app.Run();
}
catch (Exception ex) when (ex is not HostAbortedException)
{
    Log.Fatal(ex, "La aplicación falló al iniciar.");
}
finally
{
    Log.CloseAndFlush();
}
