using System.Text.Json;
using TradingBot.Core.Enums;
using TradingBot.Core.Interfaces.Trading;

namespace TradingBot.Application.Strategies.Indicators;

/// <summary>
/// Average True Range (ATR). Mide la volatilidad del mercado como promedio
/// exponencial del True Range de cada período.
/// No indica dirección, solo magnitud de movimiento.
/// </summary>
internal sealed class AtrIndicator : ITechnicalIndicator
{
    private readonly int _period;
    private decimal? _previousClose;
    private decimal? _currentAtr;
    private int _count;

    public IndicatorType Type => IndicatorType.ATR;
    public string Name => $"ATR({_period})";
    public bool IsReady => _count >= _period;

    /// <summary>Valor actual del ATR, o <c>null</c> si no está listo.</summary>
    public decimal? Value => IsReady ? _currentAtr : null;

    public AtrIndicator(int period = 14)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(period, 2);
        _period = period;
    }

    /// <summary>
    /// Alimenta con el precio de cierre. Para un cálculo preciso de ATR se necesitarían
    /// High/Low/Close, pero como el bot recibe ticks individuales, se usa
    /// |close - previousClose| como aproximación del True Range.
    /// </summary>
    public void Update(decimal value)
    {
        _count++;

        if (_previousClose is null)
        {
            _previousClose = value;
            return;
        }

        // True Range simplificado: |close actual - close anterior|
        var trueRange = Math.Abs(value - _previousClose.Value);
        _previousClose = value;

        if (_currentAtr is null)
        {
            _currentAtr = trueRange;
        }
        else
        {
            // EMA del True Range (Wilder smoothing: multiplier = 1/period)
            _currentAtr = (_currentAtr.Value * (_period - 1) + trueRange) / _period;
        }
    }

    public decimal? Calculate() => IsReady ? _currentAtr : null;

    public void Reset()
    {
        _previousClose = null;
        _currentAtr = null;
        _count = 0;
    }

    public string SerializeState() => JsonSerializer.Serialize(new
    {
        _period, _previousClose, _currentAtr, _count
    });

    public bool DeserializeState(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.GetProperty("_period").GetInt32() != _period) return false;
            _previousClose = root.TryGetProperty("_previousClose", out var pc) && pc.ValueKind != JsonValueKind.Null
                ? pc.GetDecimal() : null;
            _currentAtr = root.TryGetProperty("_currentAtr", out var ca) && ca.ValueKind != JsonValueKind.Null
                ? ca.GetDecimal() : null;
            _count = root.GetProperty("_count").GetInt32();
            return true;
        }
        catch { return false; }
    }
}
