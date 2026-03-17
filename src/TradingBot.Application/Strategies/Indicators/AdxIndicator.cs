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
/// Usa Wilder smoothing (factor = 1/period) internamente.
/// Como el bot recibe solo precios de cierre (ticks), se aproximan
/// +DM/-DM usando cambios de precio consecutivos.
/// </summary>
internal sealed class AdxIndicator : ITechnicalIndicator
{
    private readonly int _period;
    private decimal? _previousPrice;
    private decimal? _prevPrevPrice;
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

    public void Update(decimal value)
    {
        _count++;

        if (_previousPrice is null)
        {
            _previousPrice = value;
            return;
        }

        if (_prevPrevPrice is null)
        {
            _prevPrevPrice = _previousPrice;
            _previousPrice = value;
            return;
        }

        // Aproximación de Directional Movement con precios de cierre:
        // upMove = current - previous, downMove = prevPrev - previous
        var upMove = value - _previousPrice.Value;
        var downMove = _prevPrevPrice.Value - _previousPrice.Value;

        var plusDm = (upMove > 0 && upMove > downMove) ? upMove : 0m;
        var minusDm = (downMove > 0 && downMove > upMove) ? downMove : 0m;

        // True Range simplificado
        var tr = Math.Abs(value - _previousPrice.Value);

        _prevPrevPrice = _previousPrice;
        _previousPrice = value;

        if (_count <= _period + 1)
        {
            // Acumular para el primer smoothing
            _smoothedPlusDm += plusDm;
            _smoothedMinusDm += minusDm;
            _smoothedTr += tr;

            if (_count == _period + 1)
            {
                // Primera media
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
        _previousPrice = null;
        _prevPrevPrice = null;
        _smoothedPlusDm = 0;
        _smoothedMinusDm = 0;
        _smoothedTr = 0;
        _smoothedAdx = 0;
        _adxInitialized = false;
        _count = 0;
    }

    public string SerializeState() => JsonSerializer.Serialize(new
    {
        _period, _previousPrice, _prevPrevPrice,
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
            _previousPrice = root.TryGetProperty("_previousPrice", out var pp) && pp.ValueKind != JsonValueKind.Null
                ? pp.GetDecimal() : null;
            _prevPrevPrice = root.TryGetProperty("_prevPrevPrice", out var ppp) && ppp.ValueKind != JsonValueKind.Null
                ? ppp.GetDecimal() : null;
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
