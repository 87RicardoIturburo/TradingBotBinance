using Microsoft.Extensions.Logging;
using TradingBot.Core.Common;
using TradingBot.Core.Entities;
using TradingBot.Core.Events;
using TradingBot.Core.Interfaces.Repositories;
using TradingBot.Core.Interfaces.Services;

namespace TradingBot.Application.RiskManagement;

/// <summary>
/// Implementación del gestor de riesgo. Valida toda orden contra la
/// <see cref="RiskConfig"/> de su estrategia antes de permitir la ejecución.
/// </summary>
internal sealed class RiskManager : IRiskManager
{
    private readonly IStrategyRepository           _strategyRepository;
    private readonly IPositionRepository           _positionRepository;
    private readonly ILogger<RiskManager>          _logger;

    public RiskManager(
        IStrategyRepository  strategyRepository,
        IPositionRepository  positionRepository,
        ILogger<RiskManager> logger)
    {
        _strategyRepository = strategyRepository;
        _positionRepository = positionRepository;
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

        // 1. Monto máximo por orden
        var orderAmount = order.Quantity.Value * (order.LimitPrice?.Value ?? 0m);
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

        _logger.LogDebug(
            "Orden {OrderId} aprobada por RiskManager (monto: {Amount}, pérdida diaria: {Loss}, posiciones: {Open})",
            order.Id, orderAmount, dailyLoss, openCount);

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
}
