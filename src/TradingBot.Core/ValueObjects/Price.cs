using TradingBot.Core.Common;

namespace TradingBot.Core.ValueObjects;

/// <summary>
/// Precio de un activo en el mercado. Siempre no negativo.
/// </summary>
public sealed record Price
{
    public decimal Value { get; }

    private Price(decimal value) => Value = value;

    public static Result<Price, DomainError> Create(decimal value)
    {
        if (value < 0)
            return Result<Price, DomainError>.Failure(
                DomainError.Validation("El precio no puede ser negativo."));

        return Result<Price, DomainError>.Success(new Price(value));
    }

    public static Price Zero => new(0m);

    public bool IsZero => Value == 0m;

    public Price Add(Price other)      => new(Value + other.Value);
    public Price Multiply(decimal factor) => new(Value * factor);

    /// <summary>
    /// Calcula el porcentaje de cambio respecto a un precio base.
    /// </summary>
    public decimal PercentageChangeTo(Price other)
        => Value == 0m ? 0m : (other.Value - Value) / Value * 100m;

    public static bool operator >(Price  left, Price right) => left.Value >  right.Value;
    public static bool operator <(Price  left, Price right) => left.Value <  right.Value;
    public static bool operator >=(Price left, Price right) => left.Value >= right.Value;
    public static bool operator <=(Price left, Price right) => left.Value <= right.Value;

    public override string ToString() => Value.ToString("F8");

    public static implicit operator decimal(Price price) => price.Value;
}
