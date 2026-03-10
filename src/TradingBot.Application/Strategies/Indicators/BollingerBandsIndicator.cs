using System.Globalization;
using TradingBot.Core.Enums;
using TradingBot.Core.Interfaces.Trading;

namespace TradingBot.Application.Strategies.Indicators;

/// <summary>
/// Bollinger Bands. Mide la volatilidad del precio.
/// <para>
/// Middle Band = SMA(period)<br/>
/// Upper Band  = Middle + stdDev × σ<br/>
/// Lower Band  = Middle − stdDev × σ
/// </para>
/// <see cref="Calculate"/> devuelve el valor de la banda media (SMA).
/// </summary>
internal sealed class BollingerBandsIndicator : ITechnicalIndicator
{
    private readonly int _period;
    private readonly decimal _stdDevMultiplier;
    private readonly Queue<decimal> _buffer;

    public IndicatorType Type => IndicatorType.BollingerBands;
    public string Name { get; }
    public bool IsReady => _buffer.Count >= _period;

    /// <summary>Banda superior (Middle + stdDev × σ).</summary>
    public decimal? UpperBand => IsReady ? MiddleBand + _stdDevMultiplier * StandardDeviation : null;

    /// <summary>Banda media (SMA).</summary>
    public decimal? MiddleBand => IsReady ? _buffer.Sum() / _period : null;

    /// <summary>Banda inferior (Middle − stdDev × σ).</summary>
    public decimal? LowerBand => IsReady ? MiddleBand - _stdDevMultiplier * StandardDeviation : null;

    /// <summary>Ancho de las bandas: (Upper − Lower) / Middle. Mide volatilidad relativa.</summary>
    public decimal? BandWidth
    {
        get
        {
            if (!IsReady) return null;
            var middle = MiddleBand!.Value;
            return middle == 0m ? 0m : (UpperBand!.Value - LowerBand!.Value) / middle;
        }
    }

    public BollingerBandsIndicator(int period = 20, decimal stdDevMultiplier = 2m)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(period, 2);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(stdDevMultiplier, 0m);

        _period           = period;
        _stdDevMultiplier = stdDevMultiplier;
        _buffer           = new Queue<decimal>(period + 1);

        Name = string.Format(CultureInfo.InvariantCulture, "BB({0},{1:F1})", period, stdDevMultiplier);
    }

    public void Update(decimal value)
    {
        _buffer.Enqueue(value);
        if (_buffer.Count > _period)
            _buffer.Dequeue();
    }

    /// <summary>Devuelve la banda media (SMA) como valor principal del indicador.</summary>
    public decimal? Calculate() => MiddleBand;

    public void Reset() => _buffer.Clear();

    private decimal StandardDeviation
    {
        get
        {
            var mean = _buffer.Sum() / _period;
            var sumOfSquares = _buffer.Sum(v => (v - mean) * (v - mean));
            return (decimal)Math.Sqrt((double)(sumOfSquares / _period));
        }
    }
}
