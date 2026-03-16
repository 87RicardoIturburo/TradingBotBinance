namespace TradingBot.Application.RiskManagement;

/// <summary>
/// Configuración de comisiones y slippage para simulaciones (Paper Trading y Backtest).
/// Sección de configuración: <c>TradingFees</c>.
/// </summary>
public sealed class TradingFeeConfig
{
    public const string SectionName = "TradingFees";

    /// <summary>Comisión maker (limit orders). Default Binance: 0.1%.</summary>
    public decimal MakerFeePercent { get; set; } = 0.001m;

    /// <summary>Comisión taker (market orders). Default Binance: 0.1%.</summary>
    public decimal TakerFeePercent { get; set; } = 0.001m;

    /// <summary>Si <c>true</c>, aplica descuento BNB (25% menos). Default: false.</summary>
    public bool UseBnbDiscount { get; set; }

    /// <summary>Slippage estimado para órdenes de mercado. Default: 0.05%.</summary>
    public decimal SlippagePercent { get; set; } = 0.0005m;

    /// <summary>Comisión maker efectiva, con descuento BNB si aplica.</summary>
    public decimal EffectiveMakerFee => UseBnbDiscount ? MakerFeePercent * 0.75m : MakerFeePercent;

    /// <summary>Comisión taker efectiva, con descuento BNB si aplica.</summary>
    public decimal EffectiveTakerFee => UseBnbDiscount ? TakerFeePercent * 0.75m : TakerFeePercent;
}
