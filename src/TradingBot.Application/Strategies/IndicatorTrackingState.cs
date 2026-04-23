namespace TradingBot.Application.Strategies;

/// <summary>
/// Estado centralizado de tracking de cruces e indicadores.
/// Vive en <c>StrategyRunnerState</c> y se pasa por referencia a la estrategia activa.
/// Al cambiar de régimen, el estado se preserva (excepto <see cref="ReEntryMode"/>).
/// </summary>
internal sealed class IndicatorTrackingState
{
    public decimal? PreviousRsi { get; set; }
    public decimal? PreviousPreviousRsi { get; set; }
    public decimal? PreviousMacdHistogram { get; set; }
    public decimal? PreviousPreviousMacdHistogram { get; set; }
    public int PreviousEmaRelation { get; set; }
    public int PreviousSmaRelation { get; set; }
    public decimal LastClosePrice { get; set; }
    public decimal? RsiLow { get; set; }
    public decimal? PriceLowAtRsiLow { get; set; }
    public DateTimeOffset LastSignalAt { get; set; }
    public bool ReEntryMode { get; set; }
    public DateTimeOffset LastStopLossAt { get; set; }

    public void UpdateRsi(decimal? value)
    {
        PreviousPreviousRsi = PreviousRsi;
        PreviousRsi = value;
    }

    public void UpdateMacd(decimal? histogram)
    {
        PreviousPreviousMacdHistogram = PreviousMacdHistogram;
        PreviousMacdHistogram = histogram;
    }

    public void OnRegimeChange()
    {
        ReEntryMode = false;
    }

    public void Reset()
    {
        PreviousRsi = null;
        PreviousPreviousRsi = null;
        PreviousMacdHistogram = null;
        PreviousPreviousMacdHistogram = null;
        PreviousEmaRelation = 0;
        PreviousSmaRelation = 0;
        LastClosePrice = 0m;
        RsiLow = null;
        PriceLowAtRsiLow = null;
        LastSignalAt = default;
        ReEntryMode = false;
        LastStopLossAt = default;
    }
}
