namespace TradingBot.Application.Backtesting;

/// <summary>
/// Perfil calculado de un symbol a partir de datos históricos.
/// Contiene los ajustes necesarios para adaptar un template de estrategia
/// calibrado para BTC/ETH a cualquier otro par de trading.
/// </summary>
public sealed record SymbolProfile(
    /// <summary>Mediana de ATR% (ATR/Close) sobre las klines históricas.</summary>
    decimal MedianAtrPercent,

    /// <summary>Mediana de Bollinger BandWidth sobre las klines históricas.</summary>
    decimal MedianBandWidth,

    /// <summary>Spread actual bid-ask del symbol (porcentaje).</summary>
    decimal CurrentSpreadPercent,

    /// <summary>Coeficiente de variación del volumen (σ/μ). 0 si no calculable.</summary>
    decimal VolumeCV,

    /// <summary>Umbral ajustado de HighVolatilityAtrPercent = MedianAtrPercent × 2.</summary>
    decimal AdjustedHighVolatilityAtrPercent,

    /// <summary>Umbral ajustado de HighVolatilityBandWidthPercent = MedianBandWidth × 2.</summary>
    decimal AdjustedHighVolatilityBandWidthPercent,

    /// <summary>MaxSpreadPercent ajustado = max(CurrentSpread × 3, 0.1%).</summary>
    decimal AdjustedMaxSpreadPercent,

    /// <summary>minRatio ajustado para VolumeSMA basado en el CV del volumen.</summary>
    decimal AdjustedVolumeMinRatio);
