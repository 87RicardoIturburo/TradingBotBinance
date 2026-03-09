using TradingBot.Core.Enums;
using TradingBot.Core.Interfaces.Trading;

namespace TradingBot.Application.Strategies.Indicators;

/// <summary>
/// Exponential Moving Average. Da más peso a los datos recientes que SMA.
/// El suavizado usa el factor <c>k = 2 / (period + 1)</c>.
/// </summary>
internal sealed class EmaIndicator : ITechnicalIndicator
{
    private readonly int _period;
    private readonly decimal _multiplier;
    private decimal? _currentEma;
    private int _count;

    public IndicatorType Type => IndicatorType.EMA;
    public string        Name => $"EMA({_period})";
    public bool          IsReady => _count >= _period;

    public EmaIndicator(int period)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(period, 2);
        _period     = period;
        _multiplier = 2m / (period + 1);
    }

    public void Update(decimal value)
    {
        _count++;
        if (_currentEma is null)
        {
            _currentEma = value;
        }
        else
        {
            _currentEma = (value - _currentEma.Value) * _multiplier + _currentEma.Value;
        }
    }

    public decimal? Calculate() =>
        IsReady ? _currentEma : null;

    public void Reset()
    {
        _currentEma = null;
        _count      = 0;
    }
}
