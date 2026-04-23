namespace TradingBot.Application.Strategies.Indicators;

/// <summary>
/// Detecta patrones de Higher Highs / Lower Lows comparando los últimos N máximos y mínimos
/// de velas cerradas. Usa un buffer circular de tamaño configurable.
/// </summary>
internal sealed class HigherHighLowDetector
{
    private readonly int _maxSize;
    private readonly Queue<(decimal High, decimal Low)> _buffer;

    public HigherHighLowDetector(int maxSize = 5)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxSize, 2);
        _maxSize = maxSize;
        _buffer = new Queue<(decimal, decimal)>(maxSize + 1);
    }

    public void Update(decimal high, decimal low)
    {
        if (_buffer.Count >= _maxSize)
            _buffer.Dequeue();
        _buffer.Enqueue((high, low));
    }

    public bool IsReady(int count = 3) => _buffer.Count >= count;

    public bool HasHigherHighs(int count = 3)
    {
        if (!IsReady(count))
            return false;

        var items = _buffer.ToArray();
        var start = items.Length - count;
        for (var i = start + 1; i < items.Length; i++)
        {
            if (items[i].High <= items[i - 1].High)
                return false;
        }
        return true;
    }

    public bool HasLowerLows(int count = 3)
    {
        if (!IsReady(count))
            return false;

        var items = _buffer.ToArray();
        var start = items.Length - count;
        for (var i = start + 1; i < items.Length; i++)
        {
            if (items[i].Low >= items[i - 1].Low)
                return false;
        }
        return true;
    }

    public void Reset() => _buffer.Clear();
}
