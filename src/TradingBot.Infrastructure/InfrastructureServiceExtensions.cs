using Binance.Net;
using CryptoExchange.Net.Authentication;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using TradingBot.Core.Interfaces;
using TradingBot.Core.Interfaces.Repositories;
using TradingBot.Core.Interfaces.Services;
using StackExchange.Redis;
using TradingBot.Infrastructure.Binance;
using TradingBot.Infrastructure.Caching;
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
            .AddRedisCache(configuration)
            .AddBinanceClients(configuration);

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

    // ── Redis ─────────────────────────────────────────────────────────────

    private static IServiceCollection AddRedisCache(
        this IServiceCollection services,
        IConfiguration          configuration)
    {
        var connectionString = Environment.GetEnvironmentVariable("REDIS_CONNECTION")
                               ?? configuration.GetConnectionString("Redis")
                               ?? "localhost:6379";

        services.Configure<RedisOptions>(configuration.GetSection(RedisOptions.SectionName));

        // IMP-6: intentar conectar a Redis; si falla, registrar fallback en memoria
        try
        {
            var connection = ConnectionMultiplexer.Connect(connectionString);
            connection.GetDatabase().Ping(); // Verificar conectividad

            services.AddSingleton<IConnectionMultiplexer>(connection);
            services.AddSingleton<ICacheService, RedisCacheService>();
        }
        catch (Exception)
        {
            // Redis no disponible → fallback a memoria
            services.AddSingleton<ICacheService, InMemoryCacheService>();
        }

        services.AddSingleton<IIndicatorStateStore, Cache.RedisIndicatorStateStore>();

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
        var useDemo = bool.TryParse(
                            Environment.GetEnvironmentVariable("BINANCE_USE_DEMO"), out var d)
                         ? d
                         : section.GetValue<bool?>(nameof(BinanceOptions.UseDemo)) ?? false;

        // Almacena las opciones resueltas (sin keys) para que otros servicios puedan leerlas
        var hasCredentials = !string.IsNullOrWhiteSpace(apiKey) && !string.IsNullOrWhiteSpace(apiSecret);

        services.Configure<BinanceOptions>(opt =>
        {
            opt.UseTestnet      = useTestnet;
            opt.UseDemo         = useDemo;
            opt.HasCredentials  = hasCredentials;
            // ApiKey y ApiSecret NO se almacenan en IOptions por seguridad
        });

        services.AddBinance(opts =>
        {
            if (hasCredentials)
                opts.ApiCredentials = new ApiCredentials(apiKey, apiSecret);

            // Demo tiene prioridad sobre Testnet (keys de demo.binance.com)
            if (useDemo)
                opts.Environment = BinanceEnvironment.Demo;
            else if (useTestnet)
                opts.Environment = BinanceEnvironment.Testnet;
        });

        // MarketDataService es Singleton porque mantiene conexiones WebSocket persistentes
        services.AddSingleton<IMarketDataService, MarketDataService>();

        // ExchangeInfoService — Singleton (caché Redis, sin estado mutable propio)
        services.AddSingleton<IExchangeInfoService, BinanceExchangeInfoService>();

        // AccountService — Singleton (caché Redis breve, sin estado mutable propio)
        services.AddSingleton<IAccountService, BinanceAccountService>();

        // UserDataStreamService — Singleton + HostedService (mantiene WebSocket persistente)
        services.AddSingleton<UserDataStreamService>();
        services.AddSingleton<IUserDataStreamService>(sp =>
            sp.GetRequiredService<UserDataStreamService>());
        services.AddHostedService(sp =>
            sp.GetRequiredService<UserDataStreamService>());

        // SpotOrderExecutor — scoped porque cada scope puede tener una orden diferente
        services.AddScoped<ISpotOrderExecutor, BinanceSpotOrderExecutor>();

        return services;
    }
}
