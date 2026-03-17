using System.Text.Json;
using TradingBot.Core.Enums;
using TradingBot.Core.Interfaces.Trading;

namespace TradingBot.Application.Strategies.Indicators;

/// <summary>
/// Fibonacci Retracement Indicator.
/// Calcula niveles de soporte/resistencia basados en el high/low de los últimos N períodos.
/// <para>
/// Niveles estándar: 0.236, 0.382, 0.500, 0.618, 0.786<br/>
/// Level = High − (High − Low) × Ratio
/// </para>
/// <see cref="Calculate"/> devuelve el nivel 0.618 (golden ratio) como valor principal.
/// </summary>
internal sealed class FibonacciIndicator : ITechnicalIndicator
{
    /// <summary>Proporciones estándar de Fibonacci usadas en trading.</summary>
    internal static readonly decimal[] Ratios = [0.236m, 0.382m, 0.500m, 0.618m, 0.786m];

    private readonly int _period;
    private readonly Queue<decimal> _buffer;

    public IndicatorType Type => IndicatorType.Fibonacci;
    public string Name { get; }
    public bool IsReady => _buffer.Count >= _period;

    /// <summary>Precio más alto en el período.</summary>
    public decimal? High => IsReady ? _buffer.Max() : null;

    /// <summary>Precio más bajo en el período.</summary>
    public decimal? Low => IsReady ? _buffer.Min() : null;

    /// <summary>
    /// Calcula todos los niveles de Fibonacci.
    /// Cada nivel = High − (High − Low) × ratio.
    /// </summary>
    public IReadOnlyDictionary<decimal, decimal>? Levels
    {
        get
        {
            if (!IsReady) return null;

            var high = _buffer.Max();
            var low  = _buffer.Min();
            var range = high - low;

            if (range == 0m)
                return Ratios.ToDictionary(r => r, _ => high);

            return Ratios.ToDictionary(r => r, r => high - range * r);
        }
    }

    public FibonacciIndicator(int period = 50)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(period, 2);

        _period = period;
        _buffer = new Queue<decimal>(period + 1);
        Name    = $"Fib({period})";
    }

    public void Update(decimal value)
    {
        _buffer.Enqueue(value);
        if (_buffer.Count > _period)
            _buffer.Dequeue();
    }

    /// <summary>Devuelve el nivel 0.618 (golden ratio) como valor principal.</summary>
    public decimal? Calculate()
    {
        if (!IsReady) return null;

        var high = _buffer.Max();
        var low  = _buffer.Min();
        var range = high - low;

        return range == 0m ? high : high - range * 0.618m;
    }

    /// <summary>
    /// Determina si el precio actual está cerca de un nivel de Fibonacci.
    /// </summary>
    /// <param name="price">Precio actual del activo.</param>
    /// <param name="tolerancePercent">Tolerancia en porcentaje (por defecto 0.5%).</param>
    /// <returns>El ratio del nivel más cercano, o null si ninguno está dentro de la tolerancia.</returns>
    public decimal? GetNearestLevel(decimal price, decimal tolerancePercent = 0.5m)
    {
        var levels = Levels;
        if (levels is null) return null;

        var tolerance = price * tolerancePercent / 100m;

        foreach (var (ratio, level) in levels)
        {
            if (Math.Abs(price - level) <= tolerance)
                return ratio;
        }

        return null;
    }

    public void Reset() => _buffer.Clear();

    public string SerializeState() => JsonSerializer.Serialize(new
    {
        _period, Buffer = _buffer.ToArray()
    });

    public bool DeserializeState(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.GetProperty("_period").GetInt32() != _period) return false;
            _buffer.Clear();
            foreach (var item in root.GetProperty("Buffer").EnumerateArray())
                _buffer.Enqueue(item.GetDecimal());
            return true;
        }
        catch { return false; }
    }
}
