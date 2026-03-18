using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using TradingBot.Core.Entities;
using TradingBot.Core.ValueObjects;

namespace TradingBot.Infrastructure.Persistence.Configurations;

internal sealed class OrderConfiguration : IEntityTypeConfiguration<Order>
{
    public void Configure(EntityTypeBuilder<Order> builder)
    {
        builder.ToTable("Orders");
        builder.HasKey(x => x.Id);

        builder.Property(x => x.StrategyId).IsRequired();

        builder.Property(x => x.Symbol)
            .HasConversion(s => s.Value, v => Symbol.Create(v).Value)
            .HasMaxLength(20)
            .IsRequired();

        builder.Property(x => x.Side).HasConversion<int>().IsRequired();
        builder.Property(x => x.Type).HasConversion<int>().IsRequired();
        builder.Property(x => x.Status).HasConversion<int>().IsRequired();
        builder.Property(x => x.Mode).HasConversion<int>().IsRequired();

        builder.Property(x => x.Quantity)
            .HasConversion(q => q.Value, v => Quantity.Create(v).Value)
            .IsRequired();

        // Propiedades opcionales — el converter solo corre en valores no-nulos
        builder.Property(x => x.LimitPrice)
            .HasConversion(p => p!.Value, v => Price.Create(v).Value);

        builder.Property(x => x.StopPrice)
            .HasConversion(p => p!.Value, v => Price.Create(v).Value);

        builder.Property(x => x.FilledQuantity)
            .HasConversion(q => q!.Value, v => Quantity.Create(v).Value);

        builder.Property(x => x.ExecutedPrice)
            .HasConversion(p => p!.Value, v => Price.Create(v).Value);

        builder.Property(x => x.BinanceOrderId)
            .HasMaxLength(64);

        builder.Property(x => x.Fee)
            .HasDefaultValue(0m)
            .IsRequired();

        builder.Property(x => x.FeeAsset)
            .HasMaxLength(10);

        // EstimatedPrice es transitorio — solo para validación de riesgo pre-ejecución
        builder.Ignore(x => x.EstimatedPrice);

        builder.Property(x => x.Version).IsConcurrencyToken();
        builder.Property(x => x.CreatedAt).IsRequired();
        builder.Property(x => x.UpdatedAt).IsRequired();

        // ── Índices ────────────────────────────────────────────────────────
        builder.HasIndex(x => x.StrategyId);
        builder.HasIndex(x => x.Symbol);
        builder.HasIndex(x => x.Status);
        builder.HasIndex(x => x.CreatedAt);
    }
}
