using System.Globalization;
using System.Text.Json;
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
/// Detecta <em>squeeze</em> (compresión de bandas) que precede movimientos explosivos.
/// <see cref="Calculate"/> devuelve el valor de la banda media (SMA).
/// </summary>
internal sealed class BollingerBandsIndicator : ITechnicalIndicator
{
    private readonly int _period;
    private readonly decimal _stdDevMultiplier;
    private readonly Queue<decimal> _buffer;
    private readonly Queue<decimal> _bandWidthHistory;
    private const int SqueezeHistoryLength = 20;
    private decimal? _previousBandWidth;
    private int _candlesSinceSqueezeRelease = -1;

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

    /// <summary>
    /// <c>true</c> cuando el BandWidth está en el percentil inferior del historial reciente,
    /// indicando compresión de volatilidad (squeeze) que precede un movimiento fuerte.
    /// </summary>
    public bool IsSqueezing
    {
        get
        {
            if (!IsReady || _bandWidthHistory.Count < SqueezeHistoryLength) return false;
            var currentBw = BandWidth!.Value;
            var sortedHistory = _bandWidthHistory.Order().ToList();
            var percentile20Index = (int)(sortedHistory.Count * 0.2m);
            return currentBw <= sortedHistory[percentile20Index];
        }
    }

    /// <summary>
    /// <c>true</c> cuando el BandWidth acaba de expandirse tras un squeeze (breakout).
    /// Detecta la transición de compresión → expansión.
    /// </summary>
    public bool SqueezeReleased
    {
        get
        {
            if (!IsReady || _bandWidthHistory.Count < SqueezeHistoryLength || _previousBandWidth is null)
                return false;

            var currentBw = BandWidth!.Value;
            var sortedHistory = _bandWidthHistory.Order().ToList();
            var percentile20Index = (int)(sortedHistory.Count * 0.2m);
            var squeezeThreshold = sortedHistory[percentile20Index];

            return _previousBandWidth.Value <= squeezeThreshold && currentBw > squeezeThreshold;
        }
    }

    /// <summary>
    /// EST-14: <c>true</c> si el squeeze se liberó en las últimas <paramref name="maxCandles"/> velas.
    /// Permite capturar breakouts que tardan 2-3 velas en desarrollarse después del squeeze release.
    /// </summary>
    public bool WasSqueezeReleasedRecently(int maxCandles = 3)
        => _candlesSinceSqueezeRelease >= 0 && _candlesSinceSqueezeRelease <= maxCandles;

    public BollingerBandsIndicator(int period = 20, decimal stdDevMultiplier = 2m)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(period, 2);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(stdDevMultiplier, 0m);

        _period           = period;
        _stdDevMultiplier = stdDevMultiplier;
        _buffer           = new Queue<decimal>(period + 1);
        _bandWidthHistory = new Queue<decimal>(SqueezeHistoryLength + 1);

        Name = string.Format(CultureInfo.InvariantCulture, "BB({0},{1:F1})", period, stdDevMultiplier);
    }

    public void Update(decimal value)
    {
        var prevBw = IsReady ? BandWidth : null;

        _buffer.Enqueue(value);
        if (_buffer.Count > _period)
            _buffer.Dequeue();

        if (IsReady)
        {
            _previousBandWidth = prevBw;

            var bw = BandWidth!.Value;
            _bandWidthHistory.Enqueue(bw);
            if (_bandWidthHistory.Count > SqueezeHistoryLength)
                _bandWidthHistory.Dequeue();

            // EST-14: rastrear velas desde el último squeeze release
            if (SqueezeReleased)
                _candlesSinceSqueezeRelease = 0;
            else if (_candlesSinceSqueezeRelease >= 0)
                _candlesSinceSqueezeRelease++;
        }
    }

    /// <summary>Devuelve la banda media (SMA) como valor principal del indicador.</summary>
    public decimal? Calculate() => MiddleBand;

    public void Reset()
    {
        _buffer.Clear();
        _bandWidthHistory.Clear();
        _previousBandWidth = null;
        _candlesSinceSqueezeRelease = -1;
    }

    public string SerializeState() => JsonSerializer.Serialize(new
    {
        _period, _stdDevMultiplier, Buffer = _buffer.ToArray(),
        BandWidthHistory = _bandWidthHistory.ToArray()
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
            _bandWidthHistory.Clear();
            if (root.TryGetProperty("BandWidthHistory", out var bwh))
            {
                foreach (var item in bwh.EnumerateArray())
                    _bandWidthHistory.Enqueue(item.GetDecimal());
            }
            return true;
        }
        catch { return false; }
    }

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
