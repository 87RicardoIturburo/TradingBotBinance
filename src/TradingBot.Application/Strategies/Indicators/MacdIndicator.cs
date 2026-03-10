using TradingBot.Core.Enums;
using TradingBot.Core.Interfaces.Trading;

namespace TradingBot.Application.Strategies.Indicators;

/// <summary>
/// Moving Average Convergence Divergence.
/// <para>
/// MACD Line  = EMA(fast) − EMA(slow)<br/>
/// Signal Line = EMA(signalPeriod) of MACD Line<br/>
/// Histogram   = MACD Line − Signal Line
/// </para>
/// <see cref="Calculate"/> devuelve el valor de la línea MACD.
/// </summary>
internal sealed class MacdIndicator : ITechnicalIndicator
{
    private readonly EmaIndicator _fastEma;
    private readonly EmaIndicator _slowEma;
    private readonly int _signalPeriod;
    private readonly decimal _signalMultiplier;
    private decimal? _signalLine;
    private int _macdCount;

    public IndicatorType Type => IndicatorType.MACD;
    public string Name { get; }
    public bool IsReady => _fastEma.IsReady && _slowEma.IsReady && _macdCount >= _signalPeriod;

    /// <summary>Valor actual de la línea de señal MACD.</summary>
    public decimal? SignalLine => IsReady ? _signalLine : null;

    /// <summary>Histograma MACD (MACD Line − Signal Line).</summary>
    public decimal? Histogram => IsReady ? Calculate() - _signalLine : null;

    public MacdIndicator(int fastPeriod = 12, int slowPeriod = 26, int signalPeriod = 9)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(fastPeriod, 2);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(slowPeriod, fastPeriod);
        ArgumentOutOfRangeException.ThrowIfLessThan(signalPeriod, 2);

        _fastEma          = new EmaIndicator(fastPeriod);
        _slowEma          = new EmaIndicator(slowPeriod);
        _signalPeriod     = signalPeriod;
        _signalMultiplier = 2m / (signalPeriod + 1);

        Name = $"MACD({fastPeriod},{slowPeriod},{signalPeriod})";
    }

    public void Update(decimal value)
    {
        _fastEma.Update(value);
        _slowEma.Update(value);

        if (!_fastEma.IsReady || !_slowEma.IsReady)
            return;

        var macdLine = _fastEma.Calculate()!.Value - _slowEma.Calculate()!.Value;
        _macdCount++;

        if (_signalLine is null)
        {
            _signalLine = macdLine;
        }
        else
        {
            _signalLine = (macdLine - _signalLine.Value) * _signalMultiplier + _signalLine.Value;
        }
    }

    public decimal? Calculate()
    {
        if (!_fastEma.IsReady || !_slowEma.IsReady)
            return null;

        return _fastEma.Calculate()!.Value - _slowEma.Calculate()!.Value;
    }

    public void Reset()
    {
        _fastEma.Reset();
        _slowEma.Reset();
        _signalLine = null;
        _macdCount  = 0;
    }
}
