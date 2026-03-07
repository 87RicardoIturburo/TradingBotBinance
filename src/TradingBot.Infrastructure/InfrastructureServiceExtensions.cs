using Binance.Net;
using CryptoExchange.Net.Authentication;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using TradingBot.Core.Interfaces;
using TradingBot.Core.Interfaces.Repositories;
using TradingBot.Core.Interfaces.Services;
using TradingBot.Infrastructure.Binance;
using TradingBot.Infrastructure.Persistence;
using TradingBot.Infrastructure.Persistence.Repositories;

namespace TradingBot.Infrastructure;

public static class InfrastructureServiceExtensions
{
    public static IServiceCollection AddInfrastructure(
        this IServiceCollection services,
        IConfiguration          configuration)
    {
        services
            .AddPostgres(configuration)
            .AddRepositories()
            .AddBinanceClients(configuration);

        // Redis y Serilog se registran en sub-paso 3.4

        return services;
    }

    // ── PostgreSQL / EF Core ───────────────────────────────────────────────

    private static IServiceCollection AddPostgres(
        this IServiceCollection services,
        IConfiguration          configuration)
    {
        var connectionString = configuration.GetConnectionString("Postgres")
            ?? throw new InvalidOperationException(
                "La cadena de conexión 'Postgres' no está configurada. " +
                "Revisa appsettings.json o la variable de entorno POSTGRES_CONNECTION.");

        services.AddDbContext<TradingBotDbContext>(options =>
            options.UseNpgsql(
                connectionString,
                npgsql => npgsql
                    .MigrationsAssembly(typeof(TradingBotDbContext).Assembly.FullName)
                    .EnableRetryOnFailure(
                        maxRetryCount: 5,
                        maxRetryDelay: TimeSpan.FromSeconds(30),
                        errorCodesToAdd: null)));

        services.AddScoped<IUnitOfWork>(sp =>
            sp.GetRequiredService<TradingBotDbContext>());

        return services;
    }

    // ── Repositorios ──────────────────────────────────────────────────────

    private static IServiceCollection AddRepositories(this IServiceCollection services)
    {
        services.AddScoped<IStrategyRepository, StrategyRepository>();
        services.AddScoped<IOrderRepository,    OrderRepository>();
        services.AddScoped<IPositionRepository, PositionRepository>();

        return services;
    }

    // ── Binance.Net ───────────────────────────────────────────────────────

    private static IServiceCollection AddBinanceClients(
        this IServiceCollection services,
        IConfiguration          configuration)
    {
        // Las keys se leen de variables de entorno primero (mayor prioridad)
        var section   = configuration.GetSection(BinanceOptions.SectionName);
        var apiKey    = Environment.GetEnvironmentVariable("BINANCE_API_KEY")
                        ?? section[nameof(BinanceOptions.ApiKey)]
                        ?? string.Empty;
        var apiSecret = Environment.GetEnvironmentVariable("BINANCE_API_SECRET")
                        ?? section[nameof(BinanceOptions.ApiSecret)]
                        ?? string.Empty;
        var useTestnet = bool.TryParse(
                            Environment.GetEnvironmentVariable("BINANCE_USE_TESTNET"), out var t)
                         ? t
                         : section.GetValue<bool?>(nameof(BinanceOptions.UseTestnet)) ?? true;

        // Almacena las opciones resueltas (sin keys) para que otros servicios puedan leerlas
        services.Configure<BinanceOptions>(opt =>
        {
            opt.UseTestnet = useTestnet;
            // ApiKey y ApiSecret NO se almacenan en IOptions por seguridad
        });

        services.AddBinance(opts =>
        {
            if (!string.IsNullOrWhiteSpace(apiKey) && !string.IsNullOrWhiteSpace(apiSecret))
                opts.ApiCredentials = new ApiCredentials(apiKey, apiSecret);

            if (useTestnet)
                opts.Environment = BinanceEnvironment.Testnet;
        });

        // MarketDataService es Singleton porque mantiene conexiones WebSocket persistentes
        services.AddSingleton<IMarketDataService, MarketDataService>();

        return services;
    }
}
