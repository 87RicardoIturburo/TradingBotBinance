using System.Text.Json;
using TradingBot.Core.Enums;
using TradingBot.Core.Interfaces.Trading;

namespace TradingBot.Application.Strategies.Indicators;

/// <summary>
/// Simple Moving Average. Promedio aritmético de los últimos <c>period</c> cierres.
/// </summary>
internal sealed class SmaIndicator : ITechnicalIndicator
{
    private readonly int            _period;
    private readonly Queue<decimal> _buffer;

    public IndicatorType Type => IndicatorType.SMA;
    public string        Name => $"SMA({_period})";
    public bool          IsReady => _buffer.Count >= _period;

    public SmaIndicator(int period)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(period, 2);
        _period = period;
        _buffer = new Queue<decimal>(period + 1);
    }

    public void Update(decimal value)
    {
        _buffer.Enqueue(value);
        if (_buffer.Count > _period)
            _buffer.Dequeue();
    }

    public decimal? Calculate() =>
        IsReady ? _buffer.Sum() / _period : null;

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
