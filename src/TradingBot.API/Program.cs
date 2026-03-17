using Microsoft.AspNetCore.RateLimiting;
using Serilog;
using TradingBot.API.Authentication;
using TradingBot.API.Health;
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
            outputTemplate: "[{Timestamp:HH:mm:ss} {Level:u3}] {SourceContext}{CorrelationId} | {Message:lj}{NewLine}{Exception}")
        .WriteTo.File(
            path: "logs/tradingbot-.log",
            rollingInterval: RollingInterval.Day,
            retainedFileCountLimit: 30,
            outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {SourceContext} CID={CorrelationId} | {Message:lj}{NewLine}{Exception}"));

    // ── Servicios ─────────────────────────────────────────────────────────
    builder.Services.AddApplication(builder.Configuration);
    builder.Services.AddInfrastructure(builder.Configuration);

    // ── Autenticación: API Key (REST programático) + Cookie (frontend BFF) ──
    builder.Services
        .AddAuthentication(options =>
        {
            // Policy scheme: si llega header X-Api-Key o query access_token → esquema ApiKey;
            // de lo contrario → esquema Cookie (frontend autenticado vía BFF).
            options.DefaultScheme = "Smart";
            options.DefaultChallengeScheme = "Smart";
        })
        .AddPolicyScheme("Smart", "API Key o Cookie", options =>
        {
            options.ForwardDefaultSelector = context =>
            {
                if (context.Request.Headers.ContainsKey(ApiKeyAuthenticationOptions.HeaderName)
                    || context.Request.Query.ContainsKey("access_token"))
                {
                    return ApiKeyAuthenticationOptions.SchemeName;
                }

                return Microsoft.AspNetCore.Authentication.Cookies
                    .CookieAuthenticationDefaults.AuthenticationScheme;
            };
        })
        .AddScheme<ApiKeyAuthenticationOptions, ApiKeyAuthenticationHandler>(
            ApiKeyAuthenticationOptions.SchemeName, _ => { })
        .AddCookie(options =>
        {
            options.Cookie.Name = "TradingBot.Auth";
            options.Cookie.HttpOnly = true;
            options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
            options.Cookie.SameSite = SameSiteMode.None; // Cross-origin frontend ↔ API
            options.ExpireTimeSpan = TimeSpan.FromHours(24);
            options.SlidingExpiration = true;
            // API: no redirigir a /login; devolver 401 directamente
            options.Events.OnRedirectToLogin = ctx =>
            {
                ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
                return Task.CompletedTask;
            };
            options.Events.OnRedirectToAccessDenied = ctx =>
            {
                ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
                return Task.CompletedTask;
            };
        });

    // Configurar la API Key post-build via IPostConfigureOptions para que
    // las overrides de tests (ConfigureAppConfiguration) se apliquen correctamente.
    builder.Services.AddSingleton<
        Microsoft.Extensions.Options.IPostConfigureOptions<ApiKeyAuthenticationOptions>,
        ConfigureApiKeyOptions>();

    builder.Services.AddAuthorization();

    // ── Rate Limiting ─────────────────────────────────────────────────────
    builder.Services.AddRateLimiter(options =>
    {
        options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

        options.AddFixedWindowLimiter("api", limiter =>
        {
            limiter.PermitLimit = 100;
            limiter.Window = TimeSpan.FromMinutes(1);
            limiter.QueueLimit = 10;
            limiter.QueueProcessingOrder = System.Threading.RateLimiting.QueueProcessingOrder.OldestFirst;
        });
    });

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
    builder.Services.AddHostedService<MetricsBroadcastService>();
    builder.Services.AddOpenApi();

    // ── Health Checks ─────────────────────────────────────────────────────
    var postgresConn = builder.Configuration.GetConnectionString("Postgres") ?? "";
    var redisConn = Environment.GetEnvironmentVariable("REDIS_CONNECTION")
                    ?? builder.Configuration.GetConnectionString("Redis")
                    ?? "localhost:6379";

    builder.Services.AddHealthChecks()
        .AddNpgSql(postgresConn, name: "postgresql", tags: ["db", "ready"])
        .AddRedis(redisConn, name: "redis", tags: ["cache", "ready"])
        .AddCheck<BinanceHealthCheck>("binance", tags: ["external", "ready"])
        .AddCheck<StrategyEngineHealthCheck>("strategy-engine", tags: ["engine", "live"]);

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
    app.UseMiddleware<CorrelationIdMiddleware>();
    app.UseMiddleware<ErrorHandlingMiddleware>();
    app.UseSerilogRequestLogging();

    if (app.Environment.IsDevelopment())
    {
        app.MapOpenApi();
    }

    app.UseCors("Frontend");
    app.UseHttpsRedirection();
    app.UseRateLimiter();

    // Autenticación — siempre se activa en el pipeline; el handler
    // devuelve NoResult si no hay key configurada, permitiendo acceso libre.
    // Si hay key, [Authorize] bloqueará requests sin X-Api-Key válido.
    var resolvedApiKey = Environment.GetEnvironmentVariable("TRADINGBOT_API_KEY")
                         ?? app.Configuration.GetValue<string>("Authentication:ApiKey")
                         ?? string.Empty;

    app.UseAuthentication();
    app.UseAuthorization();

    if (string.IsNullOrWhiteSpace(resolvedApiKey))
    {
        Log.Warning("⚠ TRADINGBOT_API_KEY no configurada. La API está ABIERTA sin autenticación. " +
                    "Configure la variable de entorno TRADINGBOT_API_KEY antes de usar en producción.");
    }

    app.MapControllers()
        .RequireRateLimiting("api");
    app.MapHub<TradingHub>("/hubs/trading")
        .RequireCors("Frontend");

    // ── Health Check endpoints ────────────────────────────────────────────
    app.MapHealthChecks("/health", new Microsoft.AspNetCore.Diagnostics.HealthChecks.HealthCheckOptions
    {
        ResponseWriter = HealthCheckResponseWriter.WriteAsync
    });
    app.MapHealthChecks("/health/ready", new Microsoft.AspNetCore.Diagnostics.HealthChecks.HealthCheckOptions
    {
        Predicate = check => check.Tags.Contains("ready"),
        ResponseWriter = HealthCheckResponseWriter.WriteAsync
    });
    app.MapHealthChecks("/health/live", new Microsoft.AspNetCore.Diagnostics.HealthChecks.HealthCheckOptions
    {
        Predicate = check => check.Tags.Contains("live"),
        ResponseWriter = HealthCheckResponseWriter.WriteAsync
    });

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
