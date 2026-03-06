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

    private RiskConfig(
        decimal maxOrderAmountUsdt,
        decimal maxDailyLossUsdt,
        Percentage stopLossPercent,
        Percentage takeProfitPercent,
        int maxOpenPositions)
    {
        MaxOrderAmountUsdt  = maxOrderAmountUsdt;
        MaxDailyLossUsdt    = maxDailyLossUsdt;
        StopLossPercent     = stopLossPercent;
        TakeProfitPercent   = takeProfitPercent;
        MaxOpenPositions    = maxOpenPositions;
    }

    public static Result<RiskConfig, DomainError> Create(
        decimal maxOrderAmountUsdt,
        decimal maxDailyLossUsdt,
        decimal stopLossPercent,
        decimal takeProfitPercent,
        int maxOpenPositions = 5)
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
            maxOpenPositions));
    }

    /// <summary>Configuración conservadora por defecto: 100 USDT/orden, SL 2%, TP 4%.</summary>
    public static RiskConfig Default => Create(
        maxOrderAmountUsdt: 100m,
        maxDailyLossUsdt:   500m,
        stopLossPercent:    2m,
        takeProfitPercent:  4m).Value;
}
