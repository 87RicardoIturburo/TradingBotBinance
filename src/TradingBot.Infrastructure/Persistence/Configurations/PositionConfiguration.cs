using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using TradingBot.Core.Entities;
using TradingBot.Core.ValueObjects;

namespace TradingBot.Infrastructure.Persistence.Configurations;

internal sealed class PositionConfiguration : IEntityTypeConfiguration<Position>
{
    public void Configure(EntityTypeBuilder<Position> builder)
    {
        builder.ToTable("Positions");
        builder.HasKey(x => x.Id);

        builder.Property(x => x.StrategyId).IsRequired();

        builder.Property(x => x.Symbol)
            .HasConversion(s => s.Value, v => Symbol.Create(v).Value)
            .HasMaxLength(20)
            .IsRequired();

        builder.Property(x => x.Side).HasConversion<int>().IsRequired();

        builder.Property(x => x.EntryPrice)
            .HasConversion(p => p.Value, v => Price.Create(v).Value)
            .IsRequired();

        builder.Property(x => x.CurrentPrice)
            .HasConversion(p => p.Value, v => Price.Create(v).Value)
            .IsRequired();

        builder.Property(x => x.HighestPriceSinceEntry)
            .HasConversion(p => p.Value, v => Price.Create(v).Value)
            .IsRequired();

        builder.Property(x => x.LowestPriceSinceEntry)
            .HasConversion(p => p.Value, v => Price.Create(v).Value)
            .IsRequired();

        builder.Property(x => x.Quantity)
            .HasConversion(q => q.Value, v => Quantity.Create(v).Value)
            .IsRequired();

        builder.Property(x => x.IsOpen).IsRequired();
        builder.Property(x => x.RealizedPnL);     // decimal? — sin conversión
        builder.Property(x => x.EntryFee).HasDefaultValue(0m);
        builder.Property(x => x.ExitFee).HasDefaultValue(0m);
        builder.Property(x => x.CloseReason).HasConversion<int>();

        builder.Property(x => x.Version).IsConcurrencyToken();
        builder.Property(x => x.OpenedAt).IsRequired();

        // ── Índices ────────────────────────────────────────────────────────
        builder.HasIndex(x => x.StrategyId);
        builder.HasIndex(x => x.Symbol);
        builder.HasIndex(x => x.IsOpen);
        builder.HasIndex(x => x.OpenedAt);
    }
}
