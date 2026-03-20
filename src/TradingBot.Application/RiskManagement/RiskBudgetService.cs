using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TradingBot.Core.Enums;
using TradingBot.Core.Interfaces.Repositories;
using TradingBot.Core.Interfaces.Services;

namespace TradingBot.Application.RiskManagement;

/// <summary>
/// Guardián de capital global. Calcula el <see cref="RiskLevel"/> actual
/// a partir del P&amp;L acumulado de todas las estrategias vs el presupuesto
/// máximo de pérdida configurado por el usuario.
/// <para>
/// Niveles de protección progresiva:
/// <list type="bullet">
///   <item><see cref="RiskLevel.Normal"/> — 0–30% del max loss consumido → operación normal</item>
///   <item><see cref="RiskLevel.Reduced"/> — 30–60% → reduce orden al 70%</item>
///   <item><see cref="RiskLevel.Critical"/> — 60–80% → reduce al 40%, max 1 posición</item>
///   <item><see cref="RiskLevel.CloseOnly"/> — 80–100% → solo cerrar, no abrir</item>
///   <item><see cref="RiskLevel.Exhausted"/> — ≥100% → kill switch total</item>
/// </list>
/// </para>
/// </summary>
internal sealed class RiskBudgetService : IRiskBudget
{
    private readonly RiskBudgetConfig _config;
    private readonly IPositionRepository _positionRepository;
    private readonly ILogger<RiskBudgetService> _logger;

    private decimal _accumulatedLoss;
    private RiskLevel _currentLevel;

    public RiskLevel CurrentLevel => _config.IsEnabled ? _currentLevel : RiskLevel.Normal;
    public decimal AccumulatedLoss => _accumulatedLoss;
    public decimal MaxLossAllowed => _config.MaxLossUsdt;

    public decimal BudgetUsedPercent => _config.MaxLossUsdt > 0
        ? _accumulatedLoss / _config.MaxLossUsdt * 100m
        : 0m;

    public decimal OrderAmountMultiplier => _currentLevel switch
    {
        RiskLevel.Normal => 1.0m,
        RiskLevel.Reduced => _config.ReducedMultiplier,
        RiskLevel.Critical => _config.CriticalMultiplier,
        RiskLevel.CloseOnly => 0m,
        RiskLevel.Exhausted => 0m,
        _ => 1.0m
    };

    public int? MaxOpenPositionsOverride => _currentLevel switch
    {
        RiskLevel.Critical => _config.CriticalMaxOpenPositions,
        RiskLevel.CloseOnly => 0,
        RiskLevel.Exhausted => 0,
        _ => null
    };

    public RiskBudgetService(
        IOptions<RiskBudgetConfig> config,
        IPositionRepository positionRepository,
        ILogger<RiskBudgetService> logger)
    {
        _config = config.Value;
        _positionRepository = positionRepository;
        _logger = logger;
    }

    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        if (!_config.IsEnabled)
            return;

        var totalPnL = await CalculateTotalPnLAsync(cancellationToken);

        _accumulatedLoss = totalPnL < 0 ? Math.Abs(totalPnL) : 0m;

        var previousLevel = _currentLevel;
        _currentLevel = CalculateLevel(_accumulatedLoss);

        if (_currentLevel != previousLevel)
        {
            _logger.LogWarning(
                "🛡️ Risk Budget: nivel cambiado {Previous} → {Current} | " +
                "Pérdida acumulada: {Loss:F2} USDT / {Max:F2} USDT ({Percent:F1}%) | " +
                "Multiplicador orden: {Mult:P0} | Max posiciones: {MaxPos}",
                previousLevel, _currentLevel,
                _accumulatedLoss, _config.MaxLossUsdt, BudgetUsedPercent,
                OrderAmountMultiplier,
                MaxOpenPositionsOverride?.ToString() ?? "sin límite");
        }
    }

    private RiskLevel CalculateLevel(decimal loss)
    {
        if (_config.MaxLossUsdt <= 0)
            return RiskLevel.Normal;

        var usedPercent = loss / _config.MaxLossUsdt * 100m;

        if (usedPercent >= 100m)
            return RiskLevel.Exhausted;
        if (usedPercent >= _config.CloseOnlyThresholdPercent)
            return RiskLevel.CloseOnly;
        if (usedPercent >= _config.CriticalThresholdPercent)
            return RiskLevel.Critical;
        if (usedPercent >= _config.ReducedThresholdPercent)
            return RiskLevel.Reduced;

        return RiskLevel.Normal;
    }

    private async Task<decimal> CalculateTotalPnLAsync(CancellationToken cancellationToken)
    {
        var from = _config.BudgetStartDate ?? DateTimeOffset.UtcNow.AddDays(-30);

        var closedPositions = await _positionRepository.GetClosedByDateRangeAsync(
            from, DateTimeOffset.UtcNow, cancellationToken);

        var realizedPnL = closedPositions
            .Where(p => p.RealizedPnL.HasValue)
            .Sum(p => p.RealizedPnL!.Value);

        var openPositions = await _positionRepository.GetOpenPositionsAsync(cancellationToken);
        var unrealizedPnL = openPositions.Sum(p => p.UnrealizedPnL);

        return realizedPnL + unrealizedPnL;
    }
}
