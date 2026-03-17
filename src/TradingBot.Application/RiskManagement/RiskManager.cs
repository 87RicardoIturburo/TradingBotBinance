using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TradingBot.Core.Common;
using TradingBot.Core.Entities;
using TradingBot.Core.Events;
using TradingBot.Core.Interfaces.Repositories;
using TradingBot.Core.Interfaces.Services;

namespace TradingBot.Application.RiskManagement;

/// <summary>
/// Implementación del gestor de riesgo. Valida toda orden contra la
/// <see cref="RiskConfig"/> de su estrategia antes de permitir la ejecución.
/// Incluye esperanza matemática y límites globales como control de seguridad.
/// </summary>
internal sealed class RiskManager : IRiskManager
{
    /// <summary>
    /// Mínimo de trades cerrados necesarios para evaluar la esperanza matemática.
    /// Un valor bajo (ej: 10) puede bloquear la estrategia prematuramente por
    /// una mala racha inicial que es estadísticamente normal.
    /// </summary>
    internal const int MinTradesForExpectancy = 30;

    /// <summary>
    /// Reserva de saldo: el 5% del monto de la orden queda como buffer para comisiones.
    /// </summary>
    private const decimal FeeBuffer = 0.05m;

    private readonly IStrategyRepository           _strategyRepository;
    private readonly IPositionRepository           _positionRepository;
    private readonly IAccountService               _accountService;
    private readonly GlobalRiskSettings             _globalRisk;
    private readonly PortfolioRiskManager           _portfolioRisk;
    private readonly ILogger<RiskManager>          _logger;

    public RiskManager(
        IStrategyRepository          strategyRepository,
        IPositionRepository          positionRepository,
        IAccountService              accountService,
        IOptions<GlobalRiskSettings> globalRiskOptions,
        PortfolioRiskManager         portfolioRiskManager,
        ILogger<RiskManager>         logger)
    {
        _strategyRepository = strategyRepository;
        _positionRepository = positionRepository;
        _accountService     = accountService;
        _globalRisk         = globalRiskOptions.Value;
        _portfolioRisk      = portfolioRiskManager;
        _logger             = logger;
    }

    public async Task<Result<bool, DomainError>> ValidateOrderAsync(
        Order order,
        CancellationToken cancellationToken = default)
    {
        var strategy = await _strategyRepository.GetByIdAsync(order.StrategyId, cancellationToken);
        if (strategy is null)
            return Result<bool, DomainError>.Failure(
                DomainError.NotFound($"Estrategia '{order.StrategyId}'"));

        var risk = strategy.RiskConfig;
        var orderAmount = order.NotionalValue;

        // 0. Balance disponible en la cuenta (sólo en modo Live/Testnet)
        if (!order.IsPaperTrade)
        {
            var quoteAsset = order.Symbol.Value.EndsWith("USDT", StringComparison.OrdinalIgnoreCase)
                             ? "USDT" : "BTC";

            var balanceResult = await _accountService.GetAvailableBalanceAsync(quoteAsset, cancellationToken);
            if (balanceResult.IsSuccess)
            {
                var required = orderAmount * (1m + FeeBuffer);
                if (balanceResult.Value < required)
                {
                    _logger.LogWarning(
                        "Orden {OrderId} bloqueada: saldo disponible {Balance:F2} {Asset} < requerido {Required:F2} (incluye buffer de comisiones)",
                        order.Id, balanceResult.Value, quoteAsset, required);

                    return Result<bool, DomainError>.Failure(
                        DomainError.RiskLimitExceeded(
                            $"Saldo insuficiente: {balanceResult.Value:F2} {quoteAsset} disponible, " +
                            $"se requieren {required:F2} {quoteAsset} (incluye {FeeBuffer:P0} de buffer para comisiones)."));
                }
            }
            else
            {
                // Fail-closed: en modo Live/Testnet, bloquear si no se puede verificar balance.
                // Sin verificación de saldo no es seguro ejecutar órdenes reales.
                _logger.LogWarning(
                    "Orden {OrderId} bloqueada: no se pudo verificar balance ({Error}). En modo Live el balance es obligatorio.",
                    order.Id, balanceResult.Error.Message);

                return Result<bool, DomainError>.Failure(
                    DomainError.RiskLimitExceeded(
                        $"No se pudo verificar el saldo de la cuenta: {balanceResult.Error.Message}. " +
                        "La orden no puede ejecutarse sin confirmación de balance."));
            }
        }

        // 1. Monto máximo por orden
        if (orderAmount > risk.MaxOrderAmountUsdt)
        {
            _logger.LogWarning(
                "Orden {OrderId} bloqueada: monto {Amount} > máximo {Max}",
                order.Id, orderAmount, risk.MaxOrderAmountUsdt);

            return Result<bool, DomainError>.Failure(
                DomainError.RiskLimitExceeded(
                    $"El monto de la orden ({orderAmount:F2} USDT) supera el máximo permitido ({risk.MaxOrderAmountUsdt:F2} USDT)."));
        }

        // 2. Pérdida diaria acumulada
        var dailyLoss = await GetDailyLossAsync(order.StrategyId, cancellationToken);
        if (dailyLoss <= -risk.MaxDailyLossUsdt)
        {
            _logger.LogWarning(
                "Orden {OrderId} bloqueada: pérdida diaria {Loss} >= límite {Limit}",
                order.Id, dailyLoss, risk.MaxDailyLossUsdt);

            return Result<bool, DomainError>.Failure(
                DomainError.RiskLimitExceeded(
                    $"La pérdida diaria acumulada ({dailyLoss:F2} USDT) alcanzó el límite ({risk.MaxDailyLossUsdt:F2} USDT)."));
        }

        // 3. Número máximo de posiciones abiertas
        var openCount = await GetOpenPositionCountAsync(order.StrategyId, cancellationToken);
        if (openCount >= risk.MaxOpenPositions)
        {
            _logger.LogWarning(
                "Orden {OrderId} bloqueada: {Open} posiciones abiertas >= límite {Max}",
                order.Id, openCount, risk.MaxOpenPositions);

            return Result<bool, DomainError>.Failure(
                DomainError.RiskLimitExceeded(
                    $"Número de posiciones abiertas ({openCount}) alcanzó el máximo ({risk.MaxOpenPositions})."));
        }

        // 4. Límite global de pérdida diaria (todas las estrategias)
        if (_globalRisk.MaxDailyLossUsdt > 0)
        {
            var globalDailyLoss = await GetGlobalDailyLossAsync(cancellationToken);
            if (globalDailyLoss <= -_globalRisk.MaxDailyLossUsdt)
            {
                _logger.LogCritical(
                    "🛑 KILL SWITCH — Orden {OrderId} bloqueada: pérdida diaria GLOBAL {Loss:F2} >= límite {Limit:F2}",
                    order.Id, globalDailyLoss, _globalRisk.MaxDailyLossUsdt);

                return Result<bool, DomainError>.Failure(
                    DomainError.RiskLimitExceeded(
                        $"Límite de pérdida diaria GLOBAL alcanzado ({globalDailyLoss:F2} USDT / -{_globalRisk.MaxDailyLossUsdt:F2} USDT). Todas las órdenes bloqueadas."));
            }
        }

        // 5. Límite global de posiciones abiertas
        if (_globalRisk.MaxGlobalOpenPositions > 0)
        {
            var globalOpen = await GetGlobalOpenPositionCountAsync(cancellationToken);
            if (globalOpen >= _globalRisk.MaxGlobalOpenPositions)
            {
                _logger.LogWarning(
                    "Orden {OrderId} bloqueada: {Open} posiciones abiertas GLOBALES >= límite {Max}",
                    order.Id, globalOpen, _globalRisk.MaxGlobalOpenPositions);

                return Result<bool, DomainError>.Failure(
                    DomainError.RiskLimitExceeded(
                        $"Posiciones abiertas globales ({globalOpen}) alcanzaron el máximo ({_globalRisk.MaxGlobalOpenPositions})."));
            }
        }

        // 6. Exposición del portafolio (concentración por símbolo, límites long/short)
        var portfolioValidation = await _portfolioRisk.ValidateExposureAsync(order, _globalRisk, cancellationToken);
        if (!portfolioValidation.IsAllowed)
        {
            _logger.LogWarning(
                "Orden {OrderId} bloqueada por exposición de portafolio: {Reason}",
                order.Id, portfolioValidation.Reason);

            return Result<bool, DomainError>.Failure(
                DomainError.RiskLimitExceeded(portfolioValidation.Reason!));
        }

        // 7. Esperanza matemática — bloquea si la estrategia es perdedora a largo plazo
        var expectancy = await GetMathematicalExpectancyAsync(order.StrategyId, cancellationToken);
        if (expectancy is not null && expectancy.Value <= 0m)
        {
            _logger.LogWarning(
                "Orden {OrderId} bloqueada: esperanza matemática {E:F4} <= 0 (estrategia no rentable)",
                order.Id, expectancy.Value);

            return Result<bool, DomainError>.Failure(
                DomainError.RiskLimitExceeded(
                    $"La esperanza matemática de la estrategia es {expectancy.Value:F4}. " +
                    $"La estrategia no es rentable a largo plazo. Revise los parámetros."));
        }

        _logger.LogDebug(
            "Orden {OrderId} aprobada por RiskManager (monto: {Amount}, pérdida diaria: {Loss}, posiciones: {Open}, E: {Expectancy})",
            order.Id, orderAmount, dailyLoss, openCount, expectancy?.ToString("F4") ?? "N/A");

        return Result<bool, DomainError>.Success(true);
    }

    public async Task<decimal> GetDailyLossAsync(
        Guid strategyId,
        CancellationToken cancellationToken = default)
        => await _positionRepository.GetDailyRealizedPnLAsync(strategyId, cancellationToken);

    public async Task<int> GetOpenPositionCountAsync(
        Guid strategyId,
        CancellationToken cancellationToken = default)
        => await _positionRepository.GetOpenPositionCountAsync(strategyId, cancellationToken);

    /// <summary>Pérdida diaria global (todas las estrategias).</summary>
    private async Task<decimal> GetGlobalDailyLossAsync(CancellationToken cancellationToken)
    {
        var today = new DateTimeOffset(DateTimeOffset.UtcNow.Date, TimeSpan.Zero);
        var positions = await _positionRepository.GetClosedByDateRangeAsync(today, DateTimeOffset.UtcNow, cancellationToken);
        return positions.Sum(p => p.RealizedPnL ?? 0m);
    }

    /// <summary>Número global de posiciones abiertas (todas las estrategias).</summary>
    private async Task<int> GetGlobalOpenPositionCountAsync(CancellationToken cancellationToken)
    {
        var openPositions = await _positionRepository.GetOpenPositionsAsync(cancellationToken);
        return openPositions.Count;
    }

    /// <summary>
    /// E = (WinRate × AvgWin) − (LossRate × AvgLoss)
    /// Devuelve null si hay menos de <see cref="MinTradesForExpectancy"/> trades cerrados.
    /// </summary>
    public async Task<decimal?> GetMathematicalExpectancyAsync(
        Guid strategyId,
        CancellationToken cancellationToken = default)
    {
        var (totalTrades, wins, totalWinAmount, totalLossAmount) =
            await _positionRepository.GetTradeStatsAsync(strategyId, cancellationToken);

        if (totalTrades < MinTradesForExpectancy)
            return null;

        var losses   = totalTrades - wins;
        var winRate  = (decimal)wins / totalTrades;
        var lossRate = (decimal)losses / totalTrades;
        var avgWin   = wins > 0 ? totalWinAmount / wins : 0m;
        var avgLoss  = losses > 0 ? totalLossAmount / losses : 0m;

        return winRate * avgWin - lossRate * avgLoss;
    }

    /// <summary>
    /// Verifica si el drawdown diario de la cuenta supera el límite configurado.
    /// Devuelve <c>true</c> si se debe activar el kill switch de portafolio.
    /// </summary>
    public async Task<(bool IsTriggered, decimal DrawdownPercent)> CheckAccountDrawdownAsync(
        CancellationToken cancellationToken = default)
    {
        if (_globalRisk.MaxAccountDrawdownPercent <= 0)
            return (false, 0m);

        var globalDailyPnL = await GetGlobalDailyLossAsync(cancellationToken);
        if (globalDailyPnL >= 0)
            return (false, 0m);

        // Obtener balance actual para estimar drawdown
        var balanceResult = await _accountService.GetAvailableBalanceAsync("USDT", cancellationToken);
        if (balanceResult.IsFailure)
            return (false, 0m);

        var currentBalance = balanceResult.Value;
        // Balance al inicio del día ≈ balance actual - P&L del día
        var startOfDayBalance = currentBalance - globalDailyPnL;
        if (startOfDayBalance <= 0)
            return (false, 0m);

        var drawdownPercent = Math.Abs(globalDailyPnL) / startOfDayBalance * 100m;
        var isTriggered = drawdownPercent >= _globalRisk.MaxAccountDrawdownPercent;

        if (isTriggered)
        {
            _logger.LogCritical(
                "🛑 CIRCUIT BREAKER — Drawdown de cuenta {Drawdown:F1}% >= límite {Limit:F1}%. " +
                "P&L diario: {PnL:F2} USDT, Balance: {Balance:F2} USDT",
                drawdownPercent, _globalRisk.MaxAccountDrawdownPercent,
                globalDailyPnL, currentBalance);
        }

        return (isTriggered, Math.Round(drawdownPercent, 2));
    }

    /// <summary>
    /// Devuelve la exposición del portafolio por símbolo y dirección.
    /// </summary>
    public async Task<(decimal TotalLongUsdt, decimal TotalShortUsdt, decimal NetUsdt)> GetPortfolioExposureAsync(
        CancellationToken cancellationToken = default)
    {
        var exposure = await _portfolioRisk.GetPortfolioExposureAsync(cancellationToken);
        return (exposure.TotalLongUsdt, exposure.TotalShortUsdt, exposure.NetUsdt);
    }

    /// <summary>
    /// Devuelve la exposición del portafolio desglosada por símbolo.
    /// </summary>
    public async Task<IReadOnlyList<(string Symbol, decimal LongUsdt, decimal ShortUsdt, decimal NetUsdt)>> GetExposureBySymbolAsync(
        CancellationToken cancellationToken = default)
    {
        var bySymbol = await _portfolioRisk.GetExposureBySymbolAsync(cancellationToken);
        return bySymbol.Values
            .Select(s => (s.Symbol, s.LongUsdt, s.ShortUsdt, s.NetUsdt))
            .ToList();
    }
}
