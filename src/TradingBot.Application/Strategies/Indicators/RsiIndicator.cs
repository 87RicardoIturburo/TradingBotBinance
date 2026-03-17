using System.Text.Json;
using TradingBot.Core.Enums;
using TradingBot.Core.Interfaces.Trading;

namespace TradingBot.Application.Strategies.Indicators;

/// <summary>
/// Relative Strength Index (0–100).
/// Mide la velocidad y magnitud de los movimientos de precio recientes.
/// Típicamente: oversold &lt; 30, overbought &gt; 70.
/// </summary>
internal sealed class RsiIndicator : ITechnicalIndicator
{
    private readonly int _period;
    private decimal _averageGain;
    private decimal _averageLoss;
    private decimal? _previousValue;
    private int _count;

    public IndicatorType Type => IndicatorType.RSI;
    public string        Name => $"RSI({_period})";
    public bool          IsReady => _count > _period;

    public RsiIndicator(int period = 14)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(period, 2);
        _period = period;
    }

    public void Update(decimal value)
    {
        if (_previousValue is null)
        {
            _previousValue = value;
            _count++;
            return;
        }

        var change = value - _previousValue.Value;
        var gain   = change > 0 ? change : 0m;
        var loss   = change < 0 ? -change : 0m;

        _count++;

        if (_count <= _period)
        {
            _averageGain += gain / _period;
            _averageLoss += loss / _period;
        }
        else
        {
            _averageGain = (_averageGain * (_period - 1) + gain) / _period;
            _averageLoss = (_averageLoss * (_period - 1) + loss) / _period;
        }

        _previousValue = value;
    }

    public decimal? Calculate()
    {
        if (!IsReady)
            return null;

        if (_averageLoss == 0m)
            return 100m;

        var rs = _averageGain / _averageLoss;
        return 100m - 100m / (1m + rs);
    }

    public void Reset()
    {
        _averageGain   = 0m;
        _averageLoss   = 0m;
        _previousValue = null;
        _count         = 0;
    }

    public string SerializeState() => JsonSerializer.Serialize(new
    {
        _period, _averageGain, _averageLoss, _previousValue, _count
    });

    public bool DeserializeState(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.GetProperty("_period").GetInt32() != _period) return false;
            _averageGain   = root.GetProperty("_averageGain").GetDecimal();
            _averageLoss   = root.GetProperty("_averageLoss").GetDecimal();
            _previousValue = root.TryGetProperty("_previousValue", out var pv) && pv.ValueKind != JsonValueKind.Null
                ? pv.GetDecimal() : null;
            _count = root.GetProperty("_count").GetInt32();
            return true;
        }
        catch { return false; }
    }
}
