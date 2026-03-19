using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TradingBot.Core.Common;
using TradingBot.Core.Entities;
using TradingBot.Core.Enums;
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
    /// DES-A fix: multiplicador sobre la taker fee real para calcular el buffer de saldo.
    /// 3× la fee cubre fee + slippage razonable. Ej: fee 0.1% → buffer 0.3%.
    /// </summary>
    private const decimal FeeBufferMultiplier = 3m;

    private readonly IStrategyRepository           _strategyRepository;
    private readonly IPositionRepository           _positionRepository;
    private readonly IAccountService               _accountService;
    private readonly GlobalRiskSettings             _globalRisk;
    private readonly PortfolioRiskManager           _portfolioRisk;
    private readonly TradingFeeConfig              _feeConfig;
    private readonly ILogger<RiskManager>          _logger;

    public RiskManager(
        IStrategyRepository          strategyRepository,
        IPositionRepository          positionRepository,
        IAccountService              accountService,
        IOptions<GlobalRiskSettings> globalRiskOptions,
        PortfolioRiskManager         portfolioRiskManager,
        IOptions<TradingFeeConfig>   feeConfig,
        ILogger<RiskManager>         logger)
    {
        _strategyRepository = strategyRepository;
        _positionRepository = positionRepository;
        _accountService     = accountService;
        _globalRisk         = globalRiskOptions.Value;
        _portfolioRisk      = portfolioRiskManager;
        _feeConfig          = feeConfig.Value;
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

        // Detectar si es una orden de salida (cierra posición existente).
        // Las exit orders NO deben bloquearse por daily loss, max positions o expectancy,
        // ya que impediría cerrar posiciones y agravaría las pérdidas (CRIT-NEW-1).
        var isExitOrder = false;
        if (order.Side == OrderSide.Sell)
        {
            var openPositions = await _positionRepository.GetOpenByStrategyIdAsync(
                order.StrategyId, cancellationToken);
            isExitOrder = openPositions.Any(p => p.Symbol == order.Symbol && p.Side == OrderSide.Buy);
        }

        // 0. Balance disponible en la cuenta (sólo en modo Live/Testnet, no para exits — CRIT-NEW-2)
        if (!order.IsPaperTrade && !isExitOrder)
        {
            var quoteAsset = Core.ValueObjects.Symbol.ExtractQuoteAsset(order.Symbol.Value);

            var balanceResult = await _accountService.GetAvailableBalanceAsync(quoteAsset, cancellationToken);
            if (balanceResult.IsSuccess)
            {
                // DES-A fix: buffer dinámico basado en la taker fee real (3× fee).
                // Ej: fee 0.1% → buffer 0.3%, en vez del anterior 5% hardcoded.
                var feeBuffer = _feeConfig.EffectiveTakerFee * FeeBufferMultiplier;
                var required = orderAmount * (1m + feeBuffer);
                if (balanceResult.Value < required)
                {
                    _logger.LogWarning(
                        "Orden {OrderId} bloqueada: saldo disponible {Balance:F2} {Asset} < requerido {Required:F2} (incluye buffer de comisiones {Buffer:P2})",
                        order.Id, balanceResult.Value, quoteAsset, required, feeBuffer);

                    return Result<bool, DomainError>.Failure(
                        DomainError.RiskLimitExceeded(
                            $"Saldo insuficiente: {balanceResult.Value:F2} {quoteAsset} disponible, " +
                            $"se requieren {required:F2} {quoteAsset} (incluye {feeBuffer:P2} de buffer para comisiones)."));
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

        // 0b. Balance virtual para Paper Trading (EST-D fix)
        if (order.IsPaperTrade && !isExitOrder && _globalRisk.PaperTradingBalanceUsdt > 0)
        {
            var openPositions = await _positionRepository.GetOpenPositionsAsync(cancellationToken);
            var exposedUsdt = openPositions
                .Where(p => p.Side == OrderSide.Buy)
                .Sum(p => p.EntryPrice.Value * p.Quantity.Value);
            var availableVirtual = _globalRisk.PaperTradingBalanceUsdt - exposedUsdt;

            if (orderAmount > availableVirtual)
            {
                _logger.LogWarning(
                    "Orden Paper {OrderId} bloqueada: balance virtual disponible {Available:F2} USDT < requerido {Amount:F2} (total virtual: {Total:F2}, expuesto: {Exposed:F2})",
                    order.Id, availableVirtual, orderAmount, _globalRisk.PaperTradingBalanceUsdt, exposedUsdt);

                return Result<bool, DomainError>.Failure(
                    DomainError.RiskLimitExceeded(
                        $"Balance virtual insuficiente: {availableVirtual:F2} USDT disponible de {_globalRisk.PaperTradingBalanceUsdt:F2} USDT (expuesto: {exposedUsdt:F2} USDT)."));
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

        // CRIT-NEW-1: las exit orders solo validan monto máximo — deben poder cerrar posiciones siempre
        if (isExitOrder)
        {
            _logger.LogDebug(
                "Orden de salida {OrderId} aprobada por RiskManager (monto: {Amount:F2}, exit order)",
                order.Id, orderAmount);
            return Result<bool, DomainError>.Success(true);
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

        // 4. Límite global de pérdida diaria (todas las estrategias, incluye unrealized — ALTA-NEW-3)
        if (_globalRisk.MaxDailyLossUsdt > 0)
        {
            var globalDailyPnL = await GetGlobalDailyPnLIncludingUnrealizedAsync(cancellationToken);
            if (globalDailyPnL <= -_globalRisk.MaxDailyLossUsdt)
            {
                _logger.LogCritical(
                    "🛑 KILL SWITCH — Orden {OrderId} bloqueada: pérdida diaria GLOBAL {Loss:F2} >= límite {Limit:F2} (incluye unrealized)",
                    order.Id, globalDailyPnL, _globalRisk.MaxDailyLossUsdt);

                return Result<bool, DomainError>.Failure(
                    DomainError.RiskLimitExceeded(
                        $"Límite de pérdida diaria GLOBAL alcanzado ({globalDailyPnL:F2} USDT / -{_globalRisk.MaxDailyLossUsdt:F2} USDT). Todas las órdenes bloqueadas."));
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
        var today = new DateTimeOffset(DateTime.UtcNow.Date, TimeSpan.Zero);
        var positions = await _positionRepository.GetClosedByDateRangeAsync(today, DateTimeOffset.UtcNow, cancellationToken);
        return positions.Sum(p => p.RealizedPnL ?? 0m);
    }

    /// <summary>PnL diario global incluyendo posiciones abiertas (realized + unrealized).</summary>
    private async Task<decimal> GetGlobalDailyPnLIncludingUnrealizedAsync(CancellationToken cancellationToken)
    {
        var realizedPnL = await GetGlobalDailyLossAsync(cancellationToken);
        var openPositions = await _positionRepository.GetOpenPositionsAsync(cancellationToken);
        var unrealizedPnL = openPositions.Sum(p => p.UnrealizedPnL);
        return realizedPnL + unrealizedPnL;
    }

    /// <summary>Número global de posiciones abiertas (todas las estrategias).</summary>
    private async Task<int> GetGlobalOpenPositionCountAsync(CancellationToken cancellationToken)
    {
        var openPositions = await _positionRepository.GetOpenPositionsAsync(cancellationToken);
        return openPositions.Count;
    }

    /// <summary>
    /// E = (WinRate × AvgWin) − (LossRate × AvgLoss)
    /// Devuelve null si hay menos de <see cref="GlobalRiskSettings.MinTradesForExpectancy"/> trades cerrados.
    /// </summary>
    public async Task<decimal?> GetMathematicalExpectancyAsync(
        Guid strategyId,
        CancellationToken cancellationToken = default)
    {
        var (totalTrades, wins, totalWinAmount, totalLossAmount) =
            await _positionRepository.GetTradeStatsAsync(strategyId, cancellationToken);

        if (totalTrades < _globalRisk.MinTradesForExpectancy)
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

        // CRIT-D fix + ALTA-NEW-3: incluir pérdidas no realizadas de posiciones abiertas
        var totalDailyPnL = await GetGlobalDailyPnLIncludingUnrealizedAsync(cancellationToken);

        if (totalDailyPnL >= 0)
            return (false, 0m);

        // Obtener balance actual para estimar drawdown
        var balanceResult = await _accountService.GetAvailableBalanceAsync("USDT", cancellationToken);
        if (balanceResult.IsFailure)
            return (false, 0m);

        var currentBalance = balanceResult.Value;
        // Balance al inicio del día ≈ balance actual - P&L total del día
        var startOfDayBalance = currentBalance - totalDailyPnL;
        if (startOfDayBalance <= 0)
            return (false, 0m);

        var drawdownPercent = Math.Abs(totalDailyPnL) / startOfDayBalance * 100m;
        var isTriggered = drawdownPercent >= _globalRisk.MaxAccountDrawdownPercent;

        if (isTriggered)
        {
            _logger.LogCritical(
                "🛑 CIRCUIT BREAKER — Drawdown de cuenta {Drawdown:F1}% >= límite {Limit:F1}%. " +
                "P&L total diario: {Total:F2} USDT, Balance: {Balance:F2} USDT",
                drawdownPercent, _globalRisk.MaxAccountDrawdownPercent,
                totalDailyPnL, currentBalance);
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
