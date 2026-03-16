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
        decimal maxSpreadPercent)
    {
        MaxOrderAmountUsdt   = maxOrderAmountUsdt;
        MaxDailyLossUsdt     = maxDailyLossUsdt;
        StopLossPercent      = stopLossPercent;
        TakeProfitPercent    = takeProfitPercent;
        MaxOpenPositions     = maxOpenPositions;
        UseAtrSizing         = useAtrSizing;
        RiskPercentPerTrade  = riskPercentPerTrade;
        AtrMultiplier        = atrMultiplier;
        UseTrailingStop      = useTrailingStop;
        TrailingStopPercent  = trailingStopPercent;
        MaxSpreadPercent     = maxSpreadPercent;
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
        decimal maxSpreadPercent = 1.0m)
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

        var stopLossResult = Percentage.Create(stopLossPercent);
        if (stopLossResult.IsFailure)
            return Result<RiskConfig, DomainError>.Failure(
                DomainError.Validation($"Stop-loss inválido: {stopLossResult.Error.Message}"));

        var takeProfitResult = Percentage.Create(takeProfitPercent);
        if (takeProfitResult.IsFailure)
            return Result<RiskConfig, DomainError>.Failure(
                DomainError.Validation($"Take-profit inválido: {takeProfitResult.Error.Message}"));

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
            maxSpreadPercent));
    }

    /// <summary>Configuración conservadora por defecto: 100 USDT/orden, SL 2%, TP 4%.</summary>
    public static RiskConfig Default => Create(
        maxOrderAmountUsdt: 100m,
        maxDailyLossUsdt:   500m,
        stopLossPercent:    2m,
        takeProfitPercent:  4m).Value;
}
