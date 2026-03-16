using TradingBot.Core.Common;
using TradingBot.Core.Enums;

namespace TradingBot.Core.ValueObjects;

/// <summary>
/// Configuración de un indicador técnico. Los parámetros se validan
/// según el tipo de indicador para garantizar valores coherentes.
/// </summary>
public sealed record IndicatorConfig
{
    public IndicatorType Type { get; init; }

    /// <summary>
    /// Parámetros específicos del indicador.
    /// RSI       → period, overbought, oversold
    /// MACD      → fastPeriod, slowPeriod, signalPeriod
    /// EMA / SMA → period
    /// Bollinger → period, stdDev
    /// </summary>
    public IReadOnlyDictionary<string, decimal> Parameters { get; init; }

    private IndicatorConfig(IndicatorType type, IReadOnlyDictionary<string, decimal> parameters)
    {
        Type       = type;
        Parameters = parameters;
    }

    /// <summary>Constructor parameterless para EF Core design-time y deserialización JSON.</summary>
#pragma warning disable CS8618 // Parameters se inicializa vía init o fábrica Create
    private IndicatorConfig() { }
#pragma warning restore CS8618

    public static Result<IndicatorConfig, DomainError> Create(
        IndicatorType type,
        Dictionary<string, decimal> parameters)
    {
        var validationError = type switch
        {
            IndicatorType.RSI              => ValidateRsi(parameters),
            IndicatorType.MACD             => ValidateMacd(parameters),
            IndicatorType.EMA              => ValidatePeriod(parameters, "EMA"),
            IndicatorType.SMA              => ValidatePeriod(parameters, "SMA"),
            IndicatorType.BollingerBands   => ValidateBollingerBands(parameters),
            IndicatorType.Fibonacci        => ValidatePeriod(parameters, "Fibonacci"),
            IndicatorType.LinearRegression => ValidatePeriod(parameters, "LinearRegression"),
            IndicatorType.ADX              => ValidatePeriod(parameters, "ADX"),
            IndicatorType.ATR              => ValidatePeriod(parameters, "ATR"),
            _                              => null
        };

        if (validationError is not null)
            return Result<IndicatorConfig, DomainError>.Failure(validationError);

        return Result<IndicatorConfig, DomainError>.Success(
            new IndicatorConfig(type, parameters.AsReadOnly()));
    }

    /// <summary>Obtiene un parámetro o devuelve el valor por defecto si no existe.</summary>
    public decimal GetParameter(string key, decimal defaultValue = 0m)
        => Parameters.TryGetValue(key, out var value) ? value : defaultValue;

    // ── Factorías predefinidas ─────────────────────────────────────────────

    public static Result<IndicatorConfig, DomainError> Rsi(int period = 14, decimal overbought = 70, decimal oversold = 30)
        => Create(IndicatorType.RSI, new() { ["period"] = period, ["overbought"] = overbought, ["oversold"] = oversold });

    public static Result<IndicatorConfig, DomainError> Macd(int fastPeriod = 12, int slowPeriod = 26, int signalPeriod = 9)
        => Create(IndicatorType.MACD, new() { ["fastPeriod"] = fastPeriod, ["slowPeriod"] = slowPeriod, ["signalPeriod"] = signalPeriod });

    public static Result<IndicatorConfig, DomainError> Ema(int period = 20)
        => Create(IndicatorType.EMA, new() { ["period"] = period });

    public static Result<IndicatorConfig, DomainError> Sma(int period = 20)
        => Create(IndicatorType.SMA, new() { ["period"] = period });

    public static Result<IndicatorConfig, DomainError> Bollinger(int period = 20, decimal stdDev = 2m)
        => Create(IndicatorType.BollingerBands, new() { ["period"] = period, ["stdDev"] = stdDev });

    public static Result<IndicatorConfig, DomainError> Fibonacci(int period = 50)
        => Create(IndicatorType.Fibonacci, new() { ["period"] = period });

    public static Result<IndicatorConfig, DomainError> LinearRegression(int period = 20)
        => Create(IndicatorType.LinearRegression, new() { ["period"] = period });

    public static Result<IndicatorConfig, DomainError> Adx(int period = 14)
        => Create(IndicatorType.ADX, new() { ["period"] = period });

    public static Result<IndicatorConfig, DomainError> Atr(int period = 14)
        => Create(IndicatorType.ATR, new() { ["period"] = period });

    // ── Validaciones privadas ──────────────────────────────────────────────

    private static DomainError? ValidateRsi(Dictionary<string, decimal> p)
    {
        if (!p.TryGetValue("period", out var period) || period < 2)
            return DomainError.Validation("RSI requiere 'period' >= 2.");

        if (p.TryGetValue("overbought", out var ob) && (ob <= 50 || ob > 100))
            return DomainError.Validation("RSI: 'overbought' debe estar entre 51 y 100.");

        if (p.TryGetValue("oversold", out var os) && (os < 0 || os >= 50))
            return DomainError.Validation("RSI: 'oversold' debe estar entre 0 y 49.");

        return null;
    }

    private static DomainError? ValidateMacd(Dictionary<string, decimal> p)
    {
        if (!p.TryGetValue("fastPeriod", out var fast) || fast < 2)
            return DomainError.Validation("MACD requiere 'fastPeriod' >= 2.");

        if (!p.TryGetValue("slowPeriod", out var slow) || slow <= fast)
            return DomainError.Validation("MACD requiere 'slowPeriod' > 'fastPeriod'.");

        if (!p.TryGetValue("signalPeriod", out var signal) || signal < 2)
            return DomainError.Validation("MACD requiere 'signalPeriod' >= 2.");

        return null;
    }

    private static DomainError? ValidatePeriod(Dictionary<string, decimal> p, string name)
    {
        if (!p.TryGetValue("period", out var period) || period < 2)
            return DomainError.Validation($"{name} requiere 'period' >= 2.");

        return null;
    }

    private static DomainError? ValidateBollingerBands(Dictionary<string, decimal> p)
    {
        if (!p.TryGetValue("period", out var period) || period < 2)
            return DomainError.Validation("Bollinger Bands requiere 'period' >= 2.");

        if (!p.TryGetValue("stdDev", out var std) || std <= 0)
            return DomainError.Validation("Bollinger Bands requiere 'stdDev' > 0.");

        return null;
    }
}
