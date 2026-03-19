using TradingBot.Core.Common;

namespace TradingBot.Core.ValueObjects;

/// <summary>
/// Parámetros de gestión de riesgo para una estrategia.
/// El RiskManager valida toda orden contra esta configuración.
/// </summary>
public sealed record RiskConfig
{
    /// <summary>Monto máximo por orden en USDT.</summary>
    public decimal MaxOrderAmountUsdt { get; }

    /// <summary>Pérdida máxima acumulada diaria en USDT antes de pausar la estrategia.</summary>
    public decimal MaxDailyLossUsdt { get; }

    /// <summary>Stop-loss automático expresado como porcentaje del precio de entrada.</summary>
    public Percentage StopLossPercent { get; }

    /// <summary>Take-profit expresado como porcentaje del precio de entrada.</summary>
    public Percentage TakeProfitPercent { get; }

    /// <summary>Número máximo de posiciones abiertas simultáneas para esta estrategia.</summary>
    public int MaxOpenPositions { get; }

    /// <summary>Si <c>true</c>, el tamaño de posición se calcula dinámicamente con ATR.</summary>
    public bool UseAtrSizing { get; }

    /// <summary>Porcentaje del balance a arriesgar por trade (ej: 1.0 = 1%). Solo aplica si <see cref="UseAtrSizing"/> es <c>true</c>.</summary>
    public decimal RiskPercentPerTrade { get; }

    /// <summary>Multiplicador del ATR para la distancia de stop-loss (ej: 2.0). Solo aplica si <see cref="UseAtrSizing"/> es <c>true</c>.</summary>
    public decimal AtrMultiplier { get; }

    /// <summary>Si <c>true</c>, el stop-loss se ajusta dinámicamente al precio máximo alcanzado (trailing stop).</summary>
    public bool UseTrailingStop { get; }

    /// <summary>Porcentaje de retroceso desde el máximo para activar el trailing stop (ej: 1.5 = 1.5%). Solo aplica si <see cref="UseTrailingStop"/> es <c>true</c>.</summary>
    public decimal TrailingStopPercent { get; }

    /// <summary>Spread máximo bid-ask permitido para órdenes Market (ej: 1.0 = 1%). Si el spread supera este valor, la orden se rechaza.</summary>
    public decimal MaxSpreadPercent { get; }

    /// <summary>
    /// Segundos máximos que una orden Limit puede permanecer abierta sin llenarse.
    /// Si se excede, la orden se cancela automáticamente.
    /// <c>0</c> = sin timeout (las Limit orders permanecen hasta cancelación manual o llenado).
    /// </summary>
    public int LimitOrderTimeoutSeconds { get; }

    /// <summary>
    /// Período de la EMA usada en el timeframe de confirmación (HTF).
    /// Solo aplica si <see cref="TradingStrategy.ConfirmationTimeframe"/> está configurado.
    /// Default: 20.
    /// </summary>
    public int ConfirmationEmaPeriod { get; }

    /// <summary>
    /// Porcentaje del intervalo de vela usado como cooldown entre señales consecutivas (0-100).
    /// Ej: 50 con timeframe 1H = cooldown de 30 minutos.
    /// Default: 50. Valor 0 = sin cooldown.
    /// </summary>
    public decimal SignalCooldownPercent { get; }

    /// <summary>Valor de ADX por encima del cual el mercado se considera en tendencia. Default: 25.</summary>
    public decimal AdxTrendingThreshold { get; }

    /// <summary>Valor de ADX por debajo del cual el mercado se considera lateral. Default: 20.</summary>
    public decimal AdxRangingThreshold { get; }

    /// <summary>BandWidth de Bollinger por encima del cual se considera alta volatilidad (0.08 = 8%). Default: 0.08.</summary>
    public decimal HighVolatilityBandWidthPercent { get; }

    /// <summary>ATR relativo al precio por encima del cual se considera alta volatilidad (0.03 = 3%). Default: 0.03.</summary>
    public decimal HighVolatilityAtrPercent { get; }

    /// <summary>
    /// Porcentaje mínimo de confirmadores que deben aprobar una señal (0-100).
    /// Default: 50 (mayoría simple). Un valor de 75 exige 3/4 confirmadores.
    /// </summary>
    public decimal MinConfirmationPercent { get; }

    /// <summary>
    /// Porcentaje de ganancia para el primer take-profit parcial (ej: 2.0 = 2%).
    /// Al alcanzarse, se cierra <see cref="TakeProfit1ClosePercent"/> de la posición.
    /// 0 = TP escalonado deshabilitado (usa TP simple).
    /// </summary>
    public decimal TakeProfit1Percent { get; }

    /// <summary>
    /// Porcentaje de la posición a cerrar en TP1 (ej: 50 = cerrar 50% de la cantidad).
    /// Default: 50.
    /// </summary>
    public decimal TakeProfit1ClosePercent { get; }

    /// <summary>
    /// Porcentaje de ganancia para el segundo take-profit parcial (ej: 5.0 = 5%).
    /// Al alcanzarse, se cierra <see cref="TakeProfit2ClosePercent"/> del remanente.
    /// 0 = solo se aplica TP1 y el resto queda con trailing stop.
    /// </summary>
    public decimal TakeProfit2Percent { get; }

    /// <summary>
    /// Porcentaje del remanente a cerrar en TP2 (ej: 60 = cerrar 60% de lo que quedaba).
    /// Default: 60.
    /// </summary>
    public decimal TakeProfit2ClosePercent { get; }

    /// <summary><c>true</c> si el take-profit escalonado está habilitado (TP1 > 0).</summary>
    public bool UseScaledTakeProfit => TakeProfit1Percent > 0 || TakeProfit1AtrMultiplier > 0;

    /// <summary>
    /// EST-16: Multiplicador ATR para TP1 dinámico (ej: 1.5 = TP1 a 1.5×ATR del entry).
    /// 0 = deshabilitado (usa <see cref="TakeProfit1Percent"/> fijo).
    /// </summary>
    public decimal TakeProfit1AtrMultiplier { get; }

    /// <summary>
    /// EST-16: Multiplicador ATR para TP2 dinámico (ej: 3.0 = TP2 a 3×ATR del entry).
    /// 0 = deshabilitado (usa <see cref="TakeProfit2Percent"/> fijo).
    /// </summary>
    public decimal TakeProfit2AtrMultiplier { get; }

    /// <summary>
    /// Número máximo de velas (klines) que una posición puede permanecer abierta sin alcanzar TP.
    /// Tras este límite, se cierra automáticamente (time-based exit).
    /// 0 = deshabilitado (sin límite de tiempo).
    /// </summary>
    public int MaxPositionDurationCandles { get; }

    /// <summary>
    /// Si <c>true</c>, las posiciones abiertas durante un régimen Trending se cierran
    /// automáticamente cuando el ADX cae debajo del umbral de ranging.
    /// Previene mantener posiciones en mercados que pierden tendencia.
    /// </summary>
    public bool ExitOnRegimeChange { get; }

    private RiskConfig(
        decimal maxOrderAmountUsdt,
        decimal maxDailyLossUsdt,
        Percentage stopLossPercent,
        Percentage takeProfitPercent,
        int maxOpenPositions,
        bool useAtrSizing,
        decimal riskPercentPerTrade,
        decimal atrMultiplier,
        bool useTrailingStop,
        decimal trailingStopPercent,
        decimal maxSpreadPercent,
        int limitOrderTimeoutSeconds,
        int confirmationEmaPeriod,
        decimal signalCooldownPercent,
        decimal adxTrendingThreshold,
        decimal adxRangingThreshold,
        decimal highVolatilityBandWidthPercent,
        decimal highVolatilityAtrPercent,
        decimal minConfirmationPercent,
        decimal takeProfit1Percent,
        decimal takeProfit1ClosePercent,
        decimal takeProfit2Percent,
        decimal takeProfit2ClosePercent,
        int maxPositionDurationCandles,
        bool exitOnRegimeChange,
        decimal takeProfit1AtrMultiplier,
        decimal takeProfit2AtrMultiplier)
    {
        MaxOrderAmountUsdt              = maxOrderAmountUsdt;
        MaxDailyLossUsdt                = maxDailyLossUsdt;
        StopLossPercent                 = stopLossPercent;
        TakeProfitPercent               = takeProfitPercent;
        MaxOpenPositions                = maxOpenPositions;
        UseAtrSizing                    = useAtrSizing;
        RiskPercentPerTrade             = riskPercentPerTrade;
        AtrMultiplier                   = atrMultiplier;
        UseTrailingStop                 = useTrailingStop;
        TrailingStopPercent             = trailingStopPercent;
        MaxSpreadPercent                = maxSpreadPercent;
        LimitOrderTimeoutSeconds        = limitOrderTimeoutSeconds;
        ConfirmationEmaPeriod           = confirmationEmaPeriod;
        SignalCooldownPercent           = signalCooldownPercent;
        AdxTrendingThreshold            = adxTrendingThreshold;
        AdxRangingThreshold             = adxRangingThreshold;
        HighVolatilityBandWidthPercent  = highVolatilityBandWidthPercent;
        HighVolatilityAtrPercent        = highVolatilityAtrPercent;
        MinConfirmationPercent          = minConfirmationPercent;
        TakeProfit1Percent              = takeProfit1Percent;
        TakeProfit1ClosePercent         = takeProfit1ClosePercent;
        TakeProfit2Percent              = takeProfit2Percent;
        TakeProfit2ClosePercent         = takeProfit2ClosePercent;
        MaxPositionDurationCandles      = maxPositionDurationCandles;
        ExitOnRegimeChange              = exitOnRegimeChange;
        TakeProfit1AtrMultiplier        = takeProfit1AtrMultiplier;
        TakeProfit2AtrMultiplier        = takeProfit2AtrMultiplier;
    }

    public static Result<RiskConfig, DomainError> Create(
        decimal maxOrderAmountUsdt,
        decimal maxDailyLossUsdt,
        decimal stopLossPercent,
        decimal takeProfitPercent,
        int maxOpenPositions = 5,
        bool useAtrSizing = false,
        decimal riskPercentPerTrade = 1m,
        decimal atrMultiplier = 2m,
        bool useTrailingStop = false,
        decimal trailingStopPercent = 1.5m,
        decimal maxSpreadPercent = 1.0m,
        int limitOrderTimeoutSeconds = 0,
        int confirmationEmaPeriod = 20,
        decimal signalCooldownPercent = 50m,
        decimal adxTrendingThreshold = 25m,
        decimal adxRangingThreshold = 20m,
        decimal highVolatilityBandWidthPercent = 0.08m,
        decimal highVolatilityAtrPercent = 0.03m,
        decimal minConfirmationPercent = 50m,
        decimal takeProfit1Percent = 0m,
        decimal takeProfit1ClosePercent = 50m,
        decimal takeProfit2Percent = 0m,
        decimal takeProfit2ClosePercent = 60m,
        int maxPositionDurationCandles = 0,
        bool exitOnRegimeChange = false,
        decimal takeProfit1AtrMultiplier = 0m,
        decimal takeProfit2AtrMultiplier = 0m)
    {
        if (maxOrderAmountUsdt <= 0)
            return Result<RiskConfig, DomainError>.Failure(
                DomainError.Validation("El monto máximo por orden debe ser mayor que cero."));

        if (maxDailyLossUsdt <= 0)
            return Result<RiskConfig, DomainError>.Failure(
                DomainError.Validation("La pérdida máxima diaria debe ser mayor que cero."));

        if (maxOpenPositions <= 0)
            return Result<RiskConfig, DomainError>.Failure(
                DomainError.Validation("El número máximo de posiciones abiertas debe ser mayor que cero."));

        if (riskPercentPerTrade <= 0 || riskPercentPerTrade > 100)
            return Result<RiskConfig, DomainError>.Failure(
                DomainError.Validation("El porcentaje de riesgo por trade debe estar entre 0 y 100."));

        if (atrMultiplier <= 0)
            return Result<RiskConfig, DomainError>.Failure(
                DomainError.Validation("El multiplicador ATR debe ser mayor que cero."));

        if (confirmationEmaPeriod < 2)
            return Result<RiskConfig, DomainError>.Failure(
                DomainError.Validation("El período de EMA de confirmación debe ser al menos 2."));

        if (signalCooldownPercent < 0 || signalCooldownPercent > 100)
            return Result<RiskConfig, DomainError>.Failure(
                DomainError.Validation("El porcentaje de cooldown de señal debe estar entre 0 y 100."));

        if (minConfirmationPercent < 0 || minConfirmationPercent > 100)
            return Result<RiskConfig, DomainError>.Failure(
                DomainError.Validation("El porcentaje mínimo de confirmación debe estar entre 0 y 100."));

        var stopLossResult = Percentage.Create(stopLossPercent);
        if (stopLossResult.IsFailure)
            return Result<RiskConfig, DomainError>.Failure(
                DomainError.Validation($"Stop-loss inválido: {stopLossResult.Error.Message}"));

        var takeProfitResult = Percentage.Create(takeProfitPercent);
        if (takeProfitResult.IsFailure)
            return Result<RiskConfig, DomainError>.Failure(
                DomainError.Validation($"Take-profit inválido: {takeProfitResult.Error.Message}"));

        if (takeProfit1Percent < 0)
            return Result<RiskConfig, DomainError>.Failure(
                DomainError.Validation("TP1 no puede ser negativo."));

        if (takeProfit2Percent > 0 && takeProfit2Percent <= takeProfit1Percent)
            return Result<RiskConfig, DomainError>.Failure(
                DomainError.Validation("TP2 debe ser mayor que TP1."));

        return Result<RiskConfig, DomainError>.Success(new RiskConfig(
            maxOrderAmountUsdt,
            maxDailyLossUsdt,
            stopLossResult.Value,
            takeProfitResult.Value,
            maxOpenPositions,
            useAtrSizing,
            riskPercentPerTrade,
            atrMultiplier,
            useTrailingStop,
            trailingStopPercent,
            maxSpreadPercent,
            limitOrderTimeoutSeconds,
            confirmationEmaPeriod,
            signalCooldownPercent,
            adxTrendingThreshold,
            adxRangingThreshold,
            highVolatilityBandWidthPercent,
            highVolatilityAtrPercent,
            minConfirmationPercent,
            takeProfit1Percent,
            Math.Clamp(takeProfit1ClosePercent, 1m, 100m),
            takeProfit2Percent,
            Math.Clamp(takeProfit2ClosePercent, 1m, 100m),
            Math.Max(maxPositionDurationCandles, 0),
            exitOnRegimeChange,
            Math.Max(takeProfit1AtrMultiplier, 0m),
            Math.Max(takeProfit2AtrMultiplier, 0m)));
    }

    /// <summary>Configuración conservadora por defecto: 100 USDT/orden, SL 2%, TP 4%.</summary>
    public static RiskConfig Default => Create(
        maxOrderAmountUsdt: 100m,
        maxDailyLossUsdt:   500m,
        stopLossPercent:    2m,
        takeProfitPercent:  4m).Value;
}
