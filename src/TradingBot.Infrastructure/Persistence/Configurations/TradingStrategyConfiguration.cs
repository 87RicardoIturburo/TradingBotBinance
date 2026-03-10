using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using TradingBot.Core.Entities;
using TradingBot.Core.Enums;
using TradingBot.Core.ValueObjects;

namespace TradingBot.Infrastructure.Persistence.Configurations;

internal sealed class TradingStrategyConfiguration : IEntityTypeConfiguration<TradingStrategy>
{
    private static readonly JsonSerializerOptions JsonOptions =
        new() { PropertyNameCaseInsensitive = true };

    public void Configure(EntityTypeBuilder<TradingStrategy> builder)
    {
        builder.ToTable("TradingStrategies");
        builder.HasKey(x => x.Id);

        builder.Property(x => x.Name)
            .HasMaxLength(100)
            .IsRequired();

        builder.Property(x => x.Description)
            .HasMaxLength(500);

        builder.Property(x => x.Symbol)
            .HasConversion(s => s.Value, v => Symbol.Create(v).Value)
            .HasMaxLength(20)
            .IsRequired();

        builder.Property(x => x.Status)
            .HasConversion<int>()
            .IsRequired();

        builder.Property(x => x.Mode)
            .HasConversion<int>()
            .IsRequired();

        builder.Property(x => x.CreatedAt).IsRequired();
        builder.Property(x => x.UpdatedAt).IsRequired();

        builder.Property(x => x.Version)
            .IsConcurrencyToken();

        // ── RiskConfig → columna jsonb ──────────────────────────────────────
        builder.Property(x => x.RiskConfig)
            .HasColumnType("jsonb")
            .IsRequired()
            .HasConversion(
                rc => JsonSerializer.Serialize(new RiskConfigDto(
                    rc.MaxOrderAmountUsdt, rc.MaxDailyLossUsdt,
                    rc.StopLossPercent.Value, rc.TakeProfitPercent.Value,
                    rc.MaxOpenPositions), JsonOptions),
                json => DeserializeRiskConfig(json));

        // ── Indicators → columna jsonb via campo _indicators ────────────────
        builder.Property<List<IndicatorConfig>>("_indicators")
            .HasColumnName("Indicators")
            .HasColumnType("jsonb")
            .IsRequired()
            .HasConversion(
                new ValueConverter<List<IndicatorConfig>, string>(
                    list => JsonSerializer.Serialize(
                        list.Select(IndicatorConfigDto.From).ToList(), JsonOptions),
                    json => DeserializeIndicators(json)),
                new ValueComparer<List<IndicatorConfig>>(
                    (a, b) => a != null && b != null && a.SequenceEqual(b),
                    v => v.Aggregate(0, (h, c) => HashCode.Combine(h, c.GetHashCode())),
                    v => v.ToList()));

        // ── SavedOptimizationRanges → columna jsonb ─────────────────────────
        builder.Ignore(x => x.SavedOptimizationRanges);

        builder.Property<List<SavedParameterRange>>("_savedOptimizationRanges")
            .HasColumnName("SavedOptimizationRanges")
            .HasColumnType("jsonb")
            .IsRequired()
            .HasDefaultValueSql("'[]'::jsonb")
            .HasConversion(
                new ValueConverter<List<SavedParameterRange>, string>(
                    list => JsonSerializer.Serialize(list, JsonOptions),
                    json => JsonSerializer.Deserialize<List<SavedParameterRange>>(json, JsonOptions) ?? new List<SavedParameterRange>()),
                new ValueComparer<List<SavedParameterRange>>(
                    (a, b) => a != null && b != null && a.SequenceEqual(b),
                    v => v.Aggregate(0, (h, c) => HashCode.Combine(h, c.GetHashCode())),
                    v => v.ToList()));

        // ── Rules → tabla propia via OwnsMany ──────────────────────────────
        builder.OwnsMany(x => x.Rules, rule =>
        {
            rule.ToTable("TradingRules");
            rule.HasKey(r => r.Id);
            rule.WithOwner().HasForeignKey(r => r.StrategyId);

            rule.Property(r => r.Name).HasMaxLength(100).IsRequired();
            rule.Property(r => r.Type).HasConversion<int>().IsRequired();
            rule.Property(r => r.IsEnabled).IsRequired();
            rule.Property(r => r.CreatedAt).IsRequired();
            rule.Property(r => r.UpdatedAt).IsRequired();

            rule.Property(r => r.Condition)
                .HasColumnType("jsonb")
                .IsRequired()
                .HasConversion(
                    c => JsonSerializer.Serialize(RuleConditionDto.From(c), JsonOptions),
                    json => JsonSerializer.Deserialize<RuleConditionDto>(json, JsonOptions)!.ToDomain());

            rule.Property(r => r.Action)
                .HasColumnType("jsonb")
                .IsRequired()
                .HasConversion(
                    a => JsonSerializer.Serialize(RuleActionDto.From(a), JsonOptions),
                    json => JsonSerializer.Deserialize<RuleActionDto>(json, JsonOptions)!.ToDomain());

            rule.HasIndex(r => r.IsEnabled);
        });

        builder.Navigation(x => x.Rules)
            .HasField("_rules")
            .UsePropertyAccessMode(PropertyAccessMode.Field);

        // ── Índices ────────────────────────────────────────────────────────
        builder.HasIndex(x => x.Symbol);
        builder.HasIndex(x => x.Status);
    }

    // ── Helpers de deserialización (evitan lambdas con cuerpo en expression trees) ──

    private static RiskConfig DeserializeRiskConfig(string json)
    {
        var d = JsonSerializer.Deserialize<RiskConfigDto>(json, JsonOptions)!;
        return RiskConfig.Create(
            d.MaxOrderAmountUsdt, d.MaxDailyLossUsdt,
            d.StopLossPercent, d.TakeProfitPercent, d.MaxOpenPositions).Value;
    }

    private static List<IndicatorConfig> DeserializeIndicators(string json)
        => (JsonSerializer.Deserialize<List<IndicatorConfigDto>>(json, JsonOptions)
            ?? new List<IndicatorConfigDto>())
            .Select(d => d.ToDomain()).ToList();

    // ── DTOs internos para serialización JSON ──────────────────────────────

    private sealed record RiskConfigDto(
        decimal MaxOrderAmountUsdt,
        decimal MaxDailyLossUsdt,
        decimal StopLossPercent,
        decimal TakeProfitPercent,
        int     MaxOpenPositions);

    private sealed record IndicatorConfigDto(
        IndicatorType                  Type,
        Dictionary<string, decimal>    Parameters)
    {
        public static IndicatorConfigDto From(IndicatorConfig c) =>
            new(c.Type, new Dictionary<string, decimal>(c.Parameters));

        public IndicatorConfig ToDomain() =>
            IndicatorConfig.Create(Type, Parameters).Value;
    }

    private sealed record RuleConditionDto(
        ConditionOperator       Operator,
        List<LeafConditionDto>  Conditions)
    {
        public static RuleConditionDto From(RuleCondition c) =>
            new(c.Operator, c.Conditions
                .Select(lc => new LeafConditionDto(lc.Indicator, lc.Comparator, lc.Value))
                .ToList());

        public RuleCondition ToDomain() =>
            new(Operator, Conditions
                .Select(lc => new LeafCondition(lc.Indicator, lc.Comparator, lc.Value))
                .ToList());
    }

    private sealed record LeafConditionDto(
        IndicatorType Indicator,
        Comparator    Comparator,
        decimal       Value);

    private sealed record RuleActionDto(
        ActionType Type,
        decimal    AmountUsdt,
        decimal?   LimitPriceOffsetPercent)
    {
        public static RuleActionDto From(RuleAction a) =>
            new(a.Type, a.AmountUsdt, a.LimitPriceOffsetPercent);

        public RuleAction ToDomain() =>
            new(Type, AmountUsdt, LimitPriceOffsetPercent);
    }
}
