using System.Text.Json;
using TradingBot.Core.Enums;
using TradingBot.Core.Interfaces.Trading;

namespace TradingBot.Application.Strategies.Indicators;

/// <summary>
/// Average Directional Index (ADX). Mide la fuerza de la tendencia (no la dirección).
/// <list type="bullet">
///   <item><c>ADX &gt; 25</c> → tendencia fuerte</item>
///   <item><c>ADX &lt; 20</c> → mercado lateral (ranging)</item>
///   <item><c>+DI &gt; -DI</c> → tendencia alcista</item>
///   <item><c>-DI &gt; +DI</c> → tendencia bajista</item>
/// </list>
/// EST-12: Implementa <see cref="IOhlcIndicator"/> para calcular +DM/-DM y True Range
/// usando High/Low reales en vez de aproximaciones con precios de cierre.
/// <see cref="Update(decimal)"/> mantiene la aproximación como fallback.
/// </summary>
internal sealed class AdxIndicator : ITechnicalIndicator, IOhlcIndicator
{
    private readonly int _period;
    private decimal? _previousHigh;
    private decimal? _previousLow;
    private decimal? _previousClose;
    private decimal _smoothedPlusDm;
    private decimal _smoothedMinusDm;
    private decimal _smoothedTr;
    private decimal _smoothedAdx;
    private int _count;
    private bool _adxInitialized;

    public IndicatorType Type => IndicatorType.ADX;
    public string Name => $"ADX({_period})";
    public bool IsReady => _count >= _period * 2;

    /// <summary>Valor actual del ADX (0–100), o <c>null</c> si no está listo.</summary>
    public decimal? Adx => IsReady ? _smoothedAdx : null;

    /// <summary>+DI actual: fuerza de la tendencia alcista.</summary>
    public decimal? PlusDi => IsReady && _smoothedTr > 0
        ? _smoothedPlusDm / _smoothedTr * 100m : null;

    /// <summary>-DI actual: fuerza de la tendencia bajista.</summary>
    public decimal? MinusDi => IsReady && _smoothedTr > 0
        ? _smoothedMinusDm / _smoothedTr * 100m : null;

    /// <summary><c>true</c> si la tendencia es alcista (+DI &gt; -DI).</summary>
    public bool IsBullish => PlusDi > MinusDi;

    /// <summary><c>true</c> si la tendencia es bajista (-DI &gt; +DI).</summary>
    public bool IsBearish => MinusDi > PlusDi;

    public AdxIndicator(int period = 14)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(period, 2);
        _period = period;
    }

    /// <summary>
    /// EST-12: Alimenta con datos OHLC completos. Calcula +DM/-DM y True Range reales:
    /// <c>+DM = max(0, CurrentHigh − PreviousHigh)</c> si &gt; −DM, else 0.
    /// <c>−DM = max(0, PreviousLow − CurrentLow)</c> si &gt; +DM, else 0.
    /// <c>TR = max(High−Low, |High−prevClose|, |Low−prevClose|)</c>.
    /// </summary>
    public void UpdateOhlc(decimal high, decimal low, decimal close)
    {
        _count++;

        if (_previousClose is null)
        {
            _previousHigh = high;
            _previousLow = low;
            _previousClose = close;
            return;
        }

        // +DM / -DM estándar con High/Low
        var upMove = high - (_previousHigh ?? high);
        var downMove = (_previousLow ?? low) - low;

        var plusDm = (upMove > 0 && upMove > downMove) ? upMove : 0m;
        var minusDm = (downMove > 0 && downMove > upMove) ? downMove : 0m;

        // True Range real
        var hl = high - low;
        var hpc = Math.Abs(high - _previousClose.Value);
        var lpc = Math.Abs(low - _previousClose.Value);
        var tr = Math.Max(hl, Math.Max(hpc, lpc));

        _previousHigh = high;
        _previousLow = low;
        _previousClose = close;

        ApplyDmAndTr(plusDm, minusDm, tr);
    }

    /// <summary>
    /// Fallback: alimenta con precio de cierre. Aproxima +DM/-DM usando cambios
    /// de precio consecutivos. Menos preciso que <see cref="UpdateOhlc"/>.
    /// </summary>
    public void Update(decimal value)
    {
        _count++;

        if (_previousClose is null)
        {
            _previousClose = value;
            _previousHigh = value;
            _previousLow = value;
            return;
        }

        var upMove = value - _previousClose.Value;
        var downMove = _previousClose.Value - value;

        var plusDm = (upMove > 0 && upMove > downMove) ? upMove : 0m;
        var minusDm = (downMove > 0 && downMove > upMove) ? downMove : 0m;

        var tr = Math.Abs(value - _previousClose.Value);

        _previousClose = value;
        _previousHigh = value;
        _previousLow = value;

        ApplyDmAndTr(plusDm, minusDm, tr);
    }

    private void ApplyDmAndTr(decimal plusDm, decimal minusDm, decimal tr)
    {
        if (_count <= _period + 1)
        {
            _smoothedPlusDm += plusDm;
            _smoothedMinusDm += minusDm;
            _smoothedTr += tr;

            if (_count == _period + 1)
            {
                _smoothedPlusDm /= _period;
                _smoothedMinusDm /= _period;
                _smoothedTr /= _period;
            }
            return;
        }

        // Wilder smoothing
        _smoothedPlusDm = (_smoothedPlusDm * (_period - 1) + plusDm) / _period;
        _smoothedMinusDm = (_smoothedMinusDm * (_period - 1) + minusDm) / _period;
        _smoothedTr = (_smoothedTr * (_period - 1) + tr) / _period;

        if (_smoothedTr == 0)
            return;

        var pdi = _smoothedPlusDm / _smoothedTr * 100m;
        var mdi = _smoothedMinusDm / _smoothedTr * 100m;

        var diSum = pdi + mdi;
        var dx = diSum > 0 ? Math.Abs(pdi - mdi) / diSum * 100m : 0m;

        if (!_adxInitialized)
        {
            _smoothedAdx = dx;
            _adxInitialized = true;
        }
        else
        {
            _smoothedAdx = (_smoothedAdx * (_period - 1) + dx) / _period;
        }
    }

    public decimal? Calculate() => Adx;

    public void Reset()
    {
        _previousHigh = null;
        _previousLow = null;
        _previousClose = null;
        _smoothedPlusDm = 0;
        _smoothedMinusDm = 0;
        _smoothedTr = 0;
        _smoothedAdx = 0;
        _adxInitialized = false;
        _count = 0;
    }

    public string SerializeState() => JsonSerializer.Serialize(new
    {
        _period, _previousHigh, _previousLow, _previousClose,
        _smoothedPlusDm, _smoothedMinusDm, _smoothedTr, _smoothedAdx,
        _count, _adxInitialized
    });

    public bool DeserializeState(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.GetProperty("_period").GetInt32() != _period) return false;
            _previousHigh = root.TryGetProperty("_previousHigh", out var ph) && ph.ValueKind != JsonValueKind.Null
                ? ph.GetDecimal() : null;
            _previousLow = root.TryGetProperty("_previousLow", out var pl) && pl.ValueKind != JsonValueKind.Null
                ? pl.GetDecimal() : null;
            _previousClose = root.TryGetProperty("_previousClose", out var pc) && pc.ValueKind != JsonValueKind.Null
                ? pc.GetDecimal()
                : root.TryGetProperty("_previousPrice", out var pp) && pp.ValueKind != JsonValueKind.Null
                    ? pp.GetDecimal() : null;
            _smoothedPlusDm  = root.GetProperty("_smoothedPlusDm").GetDecimal();
            _smoothedMinusDm = root.GetProperty("_smoothedMinusDm").GetDecimal();
            _smoothedTr      = root.GetProperty("_smoothedTr").GetDecimal();
            _smoothedAdx     = root.GetProperty("_smoothedAdx").GetDecimal();
            _count           = root.GetProperty("_count").GetInt32();
            _adxInitialized  = root.GetProperty("_adxInitialized").GetBoolean();
            return true;
        }
        catch { return false; }
    }
}
