using System.Text.Json;
using TradingBot.Core.Enums;
using TradingBot.Core.Interfaces.Trading;

namespace TradingBot.Application.Strategies.Indicators;

/// <summary>
/// Volume Simple Moving Average. Calcula el promedio de volumen de las últimas N velas.
/// <para>
/// Se usa como confirmador de señales: un volumen actual superior a 1.5× la media
/// indica fuerza detrás del movimiento. Volumen inferior a 0.5× la media sugiere
/// falta de convicción (posible fake breakout).
/// </para>
/// Implementa <see cref="IVolumeIndicator"/> para recibir datos de volumen
/// separados del precio.
/// </summary>
internal sealed class VolumeSmaIndicator : ITechnicalIndicator, IVolumeIndicator
{
    private readonly int _period;
    private readonly Queue<decimal> _buffer;
    private decimal _lastVolume;

    public IndicatorType Type => IndicatorType.Volume;
    public string Name => $"VolSMA({_period})";
    public bool IsReady => _buffer.Count >= _period;

    /// <summary>Volumen promedio de las últimas N velas.</summary>
    public decimal? AverageVolume => IsReady ? _buffer.Sum() / _period : null;

    /// <summary>Último volumen recibido.</summary>
    public decimal LastVolume => _lastVolume;

    /// <summary>
    /// Ratio entre el volumen actual y el promedio.
    /// Valores > 1.5 = volumen alto, &lt; 0.5 = volumen bajo.
    /// </summary>
    public decimal? VolumeRatio
    {
        get
        {
            if (!IsReady) return null;
            var avg = AverageVolume!.Value;
            return avg > 0 ? _lastVolume / avg : null;
        }
    }

    public VolumeSmaIndicator(int period = 20)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(period, 2);
        _period = period;
        _buffer = new Queue<decimal>(period + 1);
    }

    public void UpdateVolume(decimal volume)
    {
        _lastVolume = volume;
        _buffer.Enqueue(volume);
        if (_buffer.Count > _period)
            _buffer.Dequeue();
    }

    public void Update(decimal value) => UpdateVolume(value);

    public decimal? Calculate() => AverageVolume;

    public void Reset()
    {
        _buffer.Clear();
        _lastVolume = 0;
    }

    public string SerializeState() => JsonSerializer.Serialize(new
    {
        _period, Buffer = _buffer.ToArray(), _lastVolume
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
            _lastVolume = root.TryGetProperty("_lastVolume", out var lv) ? lv.GetDecimal() : 0;
            return true;
        }
        catch { return false; }
    }
}
