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

        return new TradingBotDbContext(options);
    }
}
