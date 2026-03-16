using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NSubstitute;
using TradingBot.Core.Common;
using TradingBot.Core.Enums;
using TradingBot.Core.Interfaces;
using TradingBot.Core.Interfaces.Repositories;
using TradingBot.Core.Interfaces.Services;
using TradingBot.Core.ValueObjects;
using TradingBot.Infrastructure.Caching;
using TradingBot.Infrastructure.Persistence;
using Order = TradingBot.Core.Entities.Order;
using TradingStrategy = TradingBot.Core.Entities.TradingStrategy;

namespace TradingBot.Integration.Tests;

/// <summary>
/// Factory personalizada que reemplaza los servicios de infraestructura
/// (Postgres, Redis, Binance) con mocks para tests de integración.
/// Una sola instancia compartida por todos los test classes vía <see cref="SharedFactoryCollection"/>.
/// </summary>
public sealed class TradingBotWebFactory : WebApplicationFactory<TradingBot.API.Controllers.StrategiesController>
{
    /// <summary>API Key de prueba usada por los tests de integración.</summary>
    public const string TestApiKey = "test-api-key-for-integration-tests";

    public IStrategyRepository MockStrategyRepo { get; } = Substitute.For<IStrategyRepository>();
    public IOrderRepository    MockOrderRepo    { get; } = Substitute.For<IOrderRepository>();
    public IPositionRepository MockPositionRepo { get; } = Substitute.For<IPositionRepository>();
    public IMarketDataService  MockMarketData   { get; } = Substitute.For<IMarketDataService>();

    /// <summary>Crea un HttpClient que envía el header X-Api-Key automáticamente.</summary>
    public HttpClient CreateAuthenticatedClient()
    {
        var client = CreateClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", TestApiKey);
        return client;
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");

        builder.ConfigureAppConfiguration((_, config) =>
        {
            config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Postgres"] = "Host=fake;Database=test;Username=test;Password=test",
                ["ConnectionStrings:Redis"]    = "fake:6379",
                ["Authentication:ApiKey"]      = TestApiKey,
            });
        });

        builder.ConfigureServices(services =>
        {
            // Remover TODAS las registraciones de EF Core/Npgsql y usar InMemory
            var efDescriptors = services
                .Where(d => d.ServiceType.FullName?.Contains("EntityFrameworkCore") == true
                         || d.ServiceType.FullName?.Contains("DbContext") == true
                         || d.ImplementationType?.FullName?.Contains("Npgsql") == true
                         || d.ServiceType == typeof(IUnitOfWork))
                .ToList();
            foreach (var d in efDescriptors)
                services.Remove(d);

            services.AddDbContext<TradingBotDbContext>(o =>
                o.UseInMemoryDatabase("TestDb_" + Guid.NewGuid()));
            services.AddScoped<IUnitOfWork>(sp =>
                sp.GetRequiredService<TradingBotDbContext>());

            // Remover Redis real y reemplazar con mock
            RemoveService<StackExchange.Redis.IConnectionMultiplexer>(services);
            RemoveService<ICacheService>(services);
            services.AddSingleton(Substitute.For<StackExchange.Redis.IConnectionMultiplexer>());
            services.AddSingleton(Substitute.For<ICacheService>());

            // Detener el StrategyEngine BackgroundService para evitar side-effects
            var hostedDescriptors = services
                .Where(d => d.ServiceType == typeof(IHostedService))
                .ToList();
            foreach (var d in hostedDescriptors)
                services.Remove(d);

            // Reemplazar IStrategyEngine con mock (evita que StrategyConfigService inicie runners)
            RemoveService<IStrategyEngine>(services);
            services.AddSingleton(Substitute.For<IStrategyEngine>());

            // Reemplazar repositorios y servicios externos con mocks
            ReplaceService(services, MockStrategyRepo);
            ReplaceService(services, MockOrderRepo);
            ReplaceService(services, MockPositionRepo);
            ReplaceService(services, MockMarketData);
        });
    }

    private static void ReplaceService<T>(IServiceCollection services, T mock) where T : class
    {
        RemoveService<T>(services);
        services.AddScoped(_ => mock);
    }

    private static void RemoveService<T>(IServiceCollection services)
    {
        var descriptors = services.Where(d => d.ServiceType == typeof(T)).ToList();
        foreach (var d in descriptors)
            services.Remove(d);
    }
}

/// <summary>Comparte una sola instancia de <see cref="TradingBotWebFactory"/> entre todos los test classes.</summary>
[CollectionDefinition(nameof(SharedFactoryCollection))]
public class SharedFactoryCollection : ICollectionFixture<TradingBotWebFactory>;

// ── System ───────────────────────────────────────────────────────────────

[Collection(nameof(SharedFactoryCollection))]
public sealed class SystemControllerTests
{
    private readonly HttpClient _client;

    public SystemControllerTests(TradingBotWebFactory factory)
    {
        _client = factory.CreateAuthenticatedClient();
    }

    [Fact]
    public async Task GetStatus_ReturnsOk()
    {
        var response = await _client.GetAsync("/api/system/status", TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Pause_ReturnsOk()
    {
        var response = await _client.PostAsync("/api/system/pause", null, TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Resume_ReturnsOk()
    {
        var response = await _client.PostAsync("/api/system/resume", null, TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }
}

// ── Strategies ───────────────────────────────────────────────────────────

[Collection(nameof(SharedFactoryCollection))]
public sealed class StrategiesControllerTests
{
    private readonly HttpClient           _client;
    private readonly TradingBotWebFactory _factory;

    public StrategiesControllerTests(TradingBotWebFactory factory)
    {
        _factory = factory;
        _client  = factory.CreateAuthenticatedClient();
    }

    [Fact]
    public async Task GetAll_ReturnsOk()
    {
        _factory.MockStrategyRepo
            .GetAllAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<TradingStrategy>>(Array.Empty<TradingStrategy>()));

        var response = await _client.GetAsync("/api/strategies", TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task GetById_WhenNotFound_Returns404()
    {
        var id = Guid.NewGuid();
        _factory.MockStrategyRepo
            .GetWithRulesAsync(id, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<TradingStrategy?>(null));

        var response = await _client.GetAsync($"/api/strategies/{id}", TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Create_WithValidPayload_Returns201()
    {
        var payload = new
        {
            Name               = "Test Strategy",
            Symbol             = "BTCUSDT",
            Mode               = "PaperTrading",
            MaxOrderAmountUsdt = 100m,
            MaxDailyLossUsdt   = 500m,
            StopLossPercent    = 2m,
            TakeProfitPercent  = 4m,
            MaxOpenPositions   = 3
        };

        _factory.MockStrategyRepo
            .AddAsync(Arg.Any<TradingStrategy>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        var response = await _client.PostAsJsonAsync("/api/strategies", payload, TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.Created);
    }
}

// ── Orders ───────────────────────────────────────────────────────────────

[Collection(nameof(SharedFactoryCollection))]
public sealed class OrdersControllerTests
{
    private readonly HttpClient           _client;
    private readonly TradingBotWebFactory _factory;

    public OrdersControllerTests(TradingBotWebFactory factory)
    {
        _factory = factory;
        _client  = factory.CreateAuthenticatedClient();
    }

    [Fact]
    public async Task GetOpen_ReturnsOk()
    {
        _factory.MockOrderRepo
            .GetOpenOrdersAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<Order>>(Array.Empty<Order>()));

        var response = await _client.GetAsync("/api/orders/open", TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Cancel_WhenNotFound_Returns404()
    {
        var id = Guid.NewGuid();
        _factory.MockOrderRepo
            .GetByIdAsync(id, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<Order?>(null));

        var response = await _client.DeleteAsync($"/api/orders/{id}", TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }
}
