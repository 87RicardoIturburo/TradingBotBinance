using System.Text.Json;
using TradingBot.Core.Enums;
using TradingBot.Core.Interfaces.Trading;

namespace TradingBot.Application.Strategies.Indicators;

/// <summary>
/// Average True Range (ATR). Mide la volatilidad del mercado como promedio
/// exponencial del True Range de cada período.
/// No indica dirección, solo magnitud de movimiento.
/// <para>
/// Implementa <see cref="IOhlcIndicator"/> para recibir datos OHLC completos y
/// calcular el True Range real: <c>max(High-Low, |High-prevClose|, |Low-prevClose|)</c>.
/// El método <see cref="Update(decimal)"/> mantiene la aproximación <c>|close-prevClose|</c>
/// como fallback cuando solo se dispone de precios de cierre.
/// </para>
/// </summary>
internal sealed class AtrIndicator : ITechnicalIndicator, IOhlcIndicator
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
    /// Alimenta con datos OHLC completos. Calcula el True Range real:
    /// <c>max(High-Low, |High-prevClose|, |Low-prevClose|)</c>.
    /// Preferido sobre <see cref="Update(decimal)"/> cuando se dispone de velas.
    /// </summary>
    public void UpdateOhlc(decimal high, decimal low, decimal close)
    {
        _count++;

        decimal trueRange;
        if (_previousClose is null)
        {
            // Primera vela: True Range = High - Low
            trueRange = high - low;
            // Si H=L (sin variación), usar 0 — el ATR se ajustará en períodos siguientes
            if (trueRange < 0) trueRange = 0;
        }
        else
        {
            // True Range = max(High-Low, |High-prevClose|, |Low-prevClose|)
            var hl = high - low;
            var hpc = Math.Abs(high - _previousClose.Value);
            var lpc = Math.Abs(low - _previousClose.Value);
            trueRange = Math.Max(hl, Math.Max(hpc, lpc));
        }

        _previousClose = close;
        ApplyTrueRange(trueRange);
    }

    /// <summary>
    /// Fallback: alimenta con precio de cierre. Usa <c>|close-prevClose|</c> como
    /// aproximación del True Range. Menos preciso que <see cref="UpdateOhlc"/>.
    /// </summary>
    public void Update(decimal value)
    {
        _count++;

        if (_previousClose is null)
        {
            _previousClose = value;
            return;
        }

        var trueRange = Math.Abs(value - _previousClose.Value);
        _previousClose = value;
        ApplyTrueRange(trueRange);
    }

    /// <summary>Aplica un True Range al cálculo de ATR con Wilder smoothing.</summary>
    private void ApplyTrueRange(decimal trueRange)
    {
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
