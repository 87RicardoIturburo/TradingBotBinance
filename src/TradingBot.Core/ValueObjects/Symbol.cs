using TradingBot.Core.Common;

namespace TradingBot.Core.ValueObjects;

/// <summary>
/// Par de trading de Binance. Ej: BTCUSDT, ETHUSDT.
/// Siempre almacenado en mayúsculas.
/// </summary>
public sealed record Symbol
{
    public string Value { get; }

    private Symbol(string value) => Value = value;

    public static Result<Symbol, DomainError> Create(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return Result<Symbol, DomainError>.Failure(
                DomainError.Validation("El símbolo no puede estar vacío."));

        var normalized = value.Trim().ToUpperInvariant();

        if (normalized.Length is < 2 or > 20)
            return Result<Symbol, DomainError>.Failure(
                DomainError.Validation("El símbolo debe tener entre 2 y 20 caracteres."));

        if (!normalized.All(char.IsLetterOrDigit))
            return Result<Symbol, DomainError>.Failure(
                DomainError.Validation("El símbolo sólo puede contener letras y números."));

        return Result<Symbol, DomainError>.Success(new Symbol(normalized));
    }

    public override string ToString() => Value;

    public static implicit operator string(Symbol symbol) => symbol.Value;

    /// <summary>
    /// Extrae el quote asset del símbolo de trading (ej: BTCUSDT → USDT, ETHBNB → BNB).
    /// Soporta los principales quote assets de Binance.
    /// </summary>
    public static string ExtractQuoteAsset(string symbol)
    {
        ReadOnlySpan<char> s = symbol;
        string[] quoteAssets = ["USDT", "BUSD", "USDC", "BTC", "ETH", "BNB"];
        foreach (var qa in quoteAssets)
        {
            if (s.EndsWith(qa, StringComparison.OrdinalIgnoreCase))
                return qa;
        }
        return "USDT";
    }
}
