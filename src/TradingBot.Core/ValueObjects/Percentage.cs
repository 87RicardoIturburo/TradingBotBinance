using TradingBot.Core.Common;

namespace TradingBot.Core.ValueObjects;

/// <summary>
/// Porcentaje en el rango [0, 100]. Usado para stop-loss, take-profit y exposición.
/// </summary>
public sealed record Percentage
{
    public decimal Value { get; }

    private Percentage(decimal value) => Value = value;

    public static Result<Percentage, DomainError> Create(decimal value)
    {
        if (value < 0 || value > 100)
            return Result<Percentage, DomainError>.Failure(
                DomainError.Validation($"El porcentaje debe estar entre 0 y 100. Valor recibido: {value}."));

        return Result<Percentage, DomainError>.Success(new Percentage(value));
    }

    /// <summary>Factor decimal equivalente. Ej: 2.5% → 0.025.</summary>
    public decimal AsDecimalFactor => Value / 100m;

    public override string ToString() => $"{Value:F2}%";

    public static implicit operator decimal(Percentage pct) => pct.Value;
}
