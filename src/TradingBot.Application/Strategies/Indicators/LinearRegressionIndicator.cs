using System.Text.Json;
using TradingBot.Core.Enums;
using TradingBot.Core.Interfaces.Trading;

namespace TradingBot.Application.Strategies.Indicators;

/// <summary>
/// Linear Regression Indicator.
/// Calcula la línea de regresión de los últimos N precios usando mínimos cuadrados.
/// <para>
/// Slope (pendiente) &gt; 0 = tendencia alcista, &lt; 0 = bajista.<br/>
/// R² (coeficiente de determinación) mide la fuerza de la tendencia (0–1).
/// Un R² &gt; 0.7 indica tendencia fuerte.
/// </para>
/// <see cref="Calculate"/> devuelve el valor proyectado de la regresión en el punto actual.
/// </summary>
internal sealed class LinearRegressionIndicator : ITechnicalIndicator
{
    private readonly int _period;
    private readonly Queue<decimal> _buffer;

    public IndicatorType Type => IndicatorType.LinearRegression;
    public string Name { get; }
    public bool IsReady => _buffer.Count >= _period;

    /// <summary>
    /// Pendiente de la regresión. Positiva = tendencia alcista, negativa = bajista.
    /// Normalizada por el precio medio para ser comparable entre activos.
    /// </summary>
    public decimal? Slope
    {
        get
        {
            if (!IsReady) return null;
            var (slope, _) = ComputeRegression();
            return slope;
        }
    }

    /// <summary>
    /// Coeficiente de determinación (0–1). Mide qué tan bien la línea de regresión
    /// explica el movimiento de precio. R² &gt; 0.7 = tendencia fuerte.
    /// </summary>
    public decimal? RSquared
    {
        get
        {
            if (!IsReady) return null;
            return ComputeRSquared();
        }
    }

    public LinearRegressionIndicator(int period = 20)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(period, 2);

        _period = period;
        _buffer = new Queue<decimal>(period + 1);
        Name    = $"LinReg({period})";
    }

    public void Update(decimal value)
    {
        _buffer.Enqueue(value);
        if (_buffer.Count > _period)
            _buffer.Dequeue();
    }

    /// <summary>Devuelve el valor proyectado de la regresión en el último punto.</summary>
    public decimal? Calculate()
    {
        if (!IsReady) return null;

        var (slope, intercept) = ComputeRegression();
        // Proyección en el último punto (x = _period - 1)
        return intercept + slope * (_period - 1);
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

    /// <summary>
    /// Mínimos cuadrados ordinarios: y = intercept + slope * x
    /// donde x = 0, 1, 2, ..., n-1
    /// </summary>
    private (decimal Slope, decimal Intercept) ComputeRegression()
    {
        var data = _buffer.ToArray();
        var n    = data.Length;

        // Sumatorias
        decimal sumX  = 0m, sumY  = 0m, sumXY = 0m, sumX2 = 0m;

        for (var i = 0; i < n; i++)
        {
            sumX  += i;
            sumY  += data[i];
            sumXY += i * data[i];
            sumX2 += (decimal)i * i;
        }

        var denominator = n * sumX2 - sumX * sumX;

        if (denominator == 0m)
            return (0m, sumY / n);

        var slope     = (n * sumXY - sumX * sumY) / denominator;
        var intercept = (sumY - slope * sumX) / n;

        return (slope, intercept);
    }

    private decimal ComputeRSquared()
    {
        var data = _buffer.ToArray();
        var n    = data.Length;

        var (slope, intercept) = ComputeRegression();

        var meanY = data.Average();

        decimal ssRes = 0m, ssTot = 0m;

        for (var i = 0; i < n; i++)
        {
            var predicted = intercept + slope * i;
            var residual  = data[i] - predicted;
            var deviation = data[i] - meanY;

            ssRes += residual * residual;
            ssTot += deviation * deviation;
        }

        return ssTot == 0m ? 1m : 1m - ssRes / ssTot;
    }
}
