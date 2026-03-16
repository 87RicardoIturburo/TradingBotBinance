using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace TradingBot.Infrastructure.Persistence;

/// <summary>
/// Fábrica de design-time para <see cref="TradingBotDbContext"/>.
/// Permite generar migraciones EF Core sin necesidad de tener PostgreSQL
/// o Redis corriendo. Solo se usa por <c>dotnet ef migrations add</c>.
/// </summary>
internal sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<TradingBotDbContext>
{
    private const string DefaultConnectionString =
        "Host=localhost;Port=5432;Database=tradingbot;Username=postgres;Password=postgres";

    public TradingBotDbContext CreateDbContext(string[] args)
    {
        var connectionString = Environment.GetEnvironmentVariable("POSTGRES_CONNECTION")
            ?? DefaultConnectionString;

        var options = new DbContextOptionsBuilder<TradingBotDbContext>()
            .UseNpgsql(
                connectionString,
                npgsql => npgsql.MigrationsAssembly(typeof(TradingBotDbContext).Assembly.FullName))
            .Options;

        return new TradingBotDbContext(options, new NullMediator());
    }

    /// <summary>Mediator nulo para design-time — no despacha eventos.</summary>
    private sealed class NullMediator : IMediator
    {
        public Task Publish(object notification, CancellationToken ct = default) => Task.CompletedTask;
        public Task Publish<TNotification>(TNotification notification, CancellationToken ct = default)
            where TNotification : INotification => Task.CompletedTask;
        public Task<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken ct = default)
            => throw new NotSupportedException("Design-time only");
        public Task Send<TRequest>(TRequest request, CancellationToken ct = default)
            where TRequest : IRequest => throw new NotSupportedException("Design-time only");
        public Task<object?> Send(object request, CancellationToken ct = default)
            => throw new NotSupportedException("Design-time only");
        public IAsyncEnumerable<TResponse> CreateStream<TResponse>(IStreamRequest<TResponse> request, CancellationToken ct = default)
            => throw new NotSupportedException("Design-time only");
        public IAsyncEnumerable<object?> CreateStream(object request, CancellationToken ct = default)
            => throw new NotSupportedException("Design-time only");
    }
}
