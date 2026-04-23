namespace TradingBot.Application.Strategies.Indicators;

/// <summary>
/// Detecta alineación de EMAs (9, 21, 50) para determinar dirección de tendencia
/// y calcula la pendiente de EMA50 para detectar mercado lateral.
/// </summary>
internal sealed class EmaAlignmentDetector
{
    private readonly EmaIndicator _ema9;
    private readonly EmaIndicator _ema21;
    private readonly EmaIndicator _ema50;
    private decimal? _previousEma50;

    public EmaAlignmentDetector()
    {
        _ema9 = new EmaIndicator(9);
        _ema21 = new EmaIndicator(21);
        _ema50 = new EmaIndicator(50);
    }

    public bool IsReady => _ema9.IsReady && _ema21.IsReady && _ema50.IsReady;

    public bool IsBullishAligned
    {
        get
        {
            if (!IsReady) return false;
            var e9 = _ema9.Calculate()!.Value;
            var e21 = _ema21.Calculate()!.Value;
            var e50 = _ema50.Calculate()!.Value;
            return e9 > e21 && e21 > e50;
        }
    }

    public bool IsBearishAligned
    {
        get
        {
            if (!IsReady) return false;
            var e9 = _ema9.Calculate()!.Value;
            var e21 = _ema21.Calculate()!.Value;
            var e50 = _ema50.Calculate()!.Value;
            return e9 < e21 && e21 < e50;
        }
    }

    public decimal? Ema50Slope
    {
        get
        {
            if (!_ema50.IsReady || _previousEma50 is null || _previousEma50.Value == 0)
                return null;
            var current = _ema50.Calculate()!.Value;
            return (current - _previousEma50.Value) / _previousEma50.Value;
        }
    }

    public bool IsFlat(decimal threshold = 0.0005m)
    {
        var slope = Ema50Slope;
        return slope is not null && Math.Abs(slope.Value) < threshold;
    }

    public void Update(decimal price)
    {
        _previousEma50 = _ema50.IsReady ? _ema50.Calculate() : null;
        _ema9.Update(price);
        _ema21.Update(price);
        _ema50.Update(price);
    }

    public void Reset()
    {
        _ema9.Reset();
        _ema21.Reset();
        _ema50.Reset();
        _previousEma50 = null;
    }
}
