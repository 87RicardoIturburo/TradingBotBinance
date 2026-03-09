using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
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
/// </summary>
public sealed class TradingBotWebFactory : WebApplicationFactory<TradingBot.API.Controllers.StrategiesController>
{
    public IStrategyRepository  MockStrategyRepo  { get; } = Substitute.For<IStrategyRepository>();
    public IOrderRepository     MockOrderRepo     { get; } = Substitute.For<IOrderRepository>();
    public IPositionRepository  MockPositionRepo  { get; } = Substitute.For<IPositionRepository>();
    public IMarketDataService   MockMarketData    { get; } = Substitute.For<IMarketDataService>();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");

        builder.ConfigureAppConfiguration((_, config) =>
        {
            // Proveer connection strings fake para que AddInfrastructure no falle
            config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Postgres"] = "Host=fake;Database=test;Username=test;Password=test",
                ["ConnectionStrings:Redis"]    = "fake:6379",
            });
        });

        builder.ConfigureServices(services =>
        {
            // Remover DbContext real y usar InMemory
            RemoveService<DbContextOptions<TradingBotDbContext>>(services);
            RemoveService<TradingBotDbContext>(services);
            RemoveService<IUnitOfWork>(services);
            services.AddDbContext<TradingBotDbContext>(o =>
                o.UseInMemoryDatabase("TestDb_" + Guid.NewGuid()));
            services.AddScoped<IUnitOfWork>(sp =>
                sp.GetRequiredService<TradingBotDbContext>());

            // Remover Redis real y reemplazar con mock
            RemoveService<StackExchange.Redis.IConnectionMultiplexer>(services);
            RemoveService<ICacheService>(services);
            services.AddSingleton(Substitute.For<StackExchange.Redis.IConnectionMultiplexer>());
            services.AddSingleton(Substitute.For<ICacheService>());

            // Reemplazar repositorios con mocks
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

public sealed class SystemControllerTests : IClassFixture<TradingBotWebFactory>
{
    private readonly HttpClient _client;

    public SystemControllerTests(TradingBotWebFactory factory)
    {
        _client = factory.CreateClient();
    }

    [Fact]
    public async Task GetStatus_ReturnsOk()
    {
        var response = await _client.GetAsync("/api/system/status");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Pause_ReturnsOk()
    {
        var response = await _client.PostAsync("/api/system/pause", null);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Resume_ReturnsOk()
    {
        var response = await _client.PostAsync("/api/system/resume", null);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }
}

public sealed class StrategiesControllerTests : IClassFixture<TradingBotWebFactory>
{
    private readonly HttpClient          _client;
    private readonly TradingBotWebFactory _factory;

    public StrategiesControllerTests(TradingBotWebFactory factory)
    {
        _factory = factory;
        _client  = factory.CreateClient();
    }

    [Fact]
    public async Task GetAll_ReturnsOkWithEmptyList()
    {
        _factory.MockStrategyRepo
            .GetActiveStrategiesAsync(Arg.Any<CancellationToken>())
            .Returns(Array.Empty<TradingStrategy>());

        var response = await _client.GetAsync("/api/strategies");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task GetById_WhenNotFound_Returns404()
    {
        var id = Guid.NewGuid();
        _factory.MockStrategyRepo
            .GetWithRulesAsync(id, Arg.Any<CancellationToken>())
            .Returns((TradingStrategy?)null);

        var response = await _client.GetAsync($"/api/strategies/{id}");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }
}

public sealed class OrdersControllerTests : IClassFixture<TradingBotWebFactory>
{
    private readonly HttpClient          _client;
    private readonly TradingBotWebFactory _factory;

    public OrdersControllerTests(TradingBotWebFactory factory)
    {
        _factory = factory;
        _client  = factory.CreateClient();
    }

    [Fact]
    public async Task GetOpen_ReturnsOk()
    {
        _factory.MockOrderRepo
            .GetOpenOrdersAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<Order>>(Array.Empty<Order>()));

        var response = await _client.GetAsync("/api/orders/open");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Cancel_WhenNotFound_Returns404()
    {
        var id = Guid.NewGuid();
        _factory.MockOrderRepo
            .GetByIdAsync(id, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<Order?>(null));

        var response = await _client.DeleteAsync($"/api/orders/{id}");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }
}
