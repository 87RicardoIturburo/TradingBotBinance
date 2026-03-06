using TradingBot.Core.Common;

namespace TradingBot.Core.ValueObjects;

/// <summary>
/// Cantidad de un activo a operar. Siempre mayor que cero.
/// </summary>
public sealed record Quantity
{
    public decimal Value { get; }

    private Quantity(decimal value) => Value = value;

    public static Result<Quantity, DomainError> Create(decimal value)
    {
        if (value <= 0)
            return Result<Quantity, DomainError>.Failure(
                DomainError.Validation("La cantidad debe ser mayor que cero."));

        return Result<Quantity, DomainError>.Success(new Quantity(value));
    }

    public Quantity Add(Quantity other)      => new(Value + other.Value);
    public Quantity Subtract(Quantity other) => new(Math.Max(0m, Value - other.Value));

    public static bool operator >(Quantity  left, Quantity right) => left.Value >  right.Value;
    public static bool operator <(Quantity  left, Quantity right) => left.Value <  right.Value;
    public static bool operator >=(Quantity left, Quantity right) => left.Value >= right.Value;
    public static bool operator <=(Quantity left, Quantity right) => left.Value <= right.Value;

    public override string ToString() => Value.ToString("F8");

    public static implicit operator decimal(Quantity qty) => qty.Value;
}
