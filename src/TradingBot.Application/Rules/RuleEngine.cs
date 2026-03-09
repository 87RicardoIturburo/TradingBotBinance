using Microsoft.Extensions.Logging;
using TradingBot.Core.Common;
using TradingBot.Core.Entities;
using TradingBot.Core.Enums;
using TradingBot.Core.Events;
using TradingBot.Core.Interfaces.Services;
using TradingBot.Core.ValueObjects;

namespace TradingBot.Application.Rules;

/// <summary>
/// Evalúa las <see cref="TradingRule"/> de una estrategia frente a señales
/// y precios actuales, decidiendo si crear una orden.
/// </summary>
internal sealed class RuleEngine : IRuleEngine
{
    private readonly ILogger<RuleEngine> _logger;

    public RuleEngine(ILogger<RuleEngine> logger)
    {
        _logger = logger;
    }

    public Task<Result<Order?, DomainError>> EvaluateAsync(
        TradingStrategy     strategy,
        SignalGeneratedEvent signal,
        CancellationToken   cancellationToken = default)
    {
        var entryRules = strategy.Rules
            .Where(r => r.IsEnabled && r.Type == RuleType.Entry)
            .ToList();

        if (entryRules.Count == 0)
            return Task.FromResult(
                Result<Order?, DomainError>.Success(null));

        foreach (var rule in entryRules)
        {
            if (!EvaluateCondition(rule.Condition, signal))
                continue;

            _logger.LogInformation(
                "Regla '{RuleName}' activada para {Symbol} (señal: {Direction})",
                rule.Name, signal.Symbol.Value, signal.Direction);

            var orderResult = CreateOrderFromAction(
                strategy, rule.Action, signal.Direction, signal.CurrentPrice);

            return Task.FromResult(orderResult);
        }

        return Task.FromResult(
            Result<Order?, DomainError>.Success(null));
    }

    public Task<Result<Order?, DomainError>> EvaluateExitRulesAsync(
        TradingStrategy strategy,
        Position        position,
        Price           currentPrice,
        CancellationToken cancellationToken = default)
    {
        var risk = strategy.RiskConfig;

        // Stop-loss automático
        var pnlPercent = position.UnrealizedPnLPercent;
        if (pnlPercent <= -(decimal)risk.StopLossPercent)
        {
            _logger.LogWarning(
                "Stop-loss activado para posición {PositionId}: PnL {PnL:F2}% <= -{StopLoss:F2}%",
                position.Id, pnlPercent, risk.StopLossPercent.Value);

            var exitSide = position.Side == OrderSide.Buy ? OrderSide.Sell : OrderSide.Buy;
            var orderResult = Order.Create(
                strategy.Id, position.Symbol, exitSide,
                OrderType.Market, position.Quantity, strategy.Mode);

            return Task.FromResult(
                orderResult.Match<Result<Order?, DomainError>>(
                    order => Result<Order?, DomainError>.Success(order),
                    error => Result<Order?, DomainError>.Failure(error)));
        }

        // Take-profit automático
        if (pnlPercent >= (decimal)risk.TakeProfitPercent)
        {
            _logger.LogInformation(
                "Take-profit activado para posición {PositionId}: PnL {PnL:F2}% >= {TakeProfit:F2}%",
                position.Id, pnlPercent, risk.TakeProfitPercent.Value);

            var exitSide = position.Side == OrderSide.Buy ? OrderSide.Sell : OrderSide.Buy;
            var orderResult = Order.Create(
                strategy.Id, position.Symbol, exitSide,
                OrderType.Market, position.Quantity, strategy.Mode);

            return Task.FromResult(
                orderResult.Match<Result<Order?, DomainError>>(
                    order => Result<Order?, DomainError>.Success(order),
                    error => Result<Order?, DomainError>.Failure(error)));
        }

        // Evaluar reglas de salida configuradas
        var exitRules = strategy.Rules
            .Where(r => r.IsEnabled && r.Type is RuleType.Exit)
            .ToList();

        foreach (var rule in exitRules)
        {
            var signal = new SignalGeneratedEvent(
                strategy.Id, position.Symbol,
                position.Side == OrderSide.Buy ? OrderSide.Sell : OrderSide.Buy,
                currentPrice, $"ExitEval|PnL={pnlPercent:F2}%");

            if (!EvaluateCondition(rule.Condition, signal))
                continue;

            _logger.LogInformation(
                "Regla de salida '{RuleName}' activada para posición {PositionId}",
                rule.Name, position.Id);

            var exitSide = position.Side == OrderSide.Buy ? OrderSide.Sell : OrderSide.Buy;
            var orderResult = Order.Create(
                strategy.Id, position.Symbol, exitSide,
                OrderType.Market, position.Quantity, strategy.Mode);

            return Task.FromResult(
                orderResult.Match<Result<Order?, DomainError>>(
                    order => Result<Order?, DomainError>.Success(order),
                    error => Result<Order?, DomainError>.Failure(error)));
        }

        return Task.FromResult(
            Result<Order?, DomainError>.Success(null));
    }

    private static bool EvaluateCondition(RuleCondition condition, SignalGeneratedEvent signal)
    {
        return condition.Operator switch
        {
            ConditionOperator.And => condition.Conditions.All(c => EvaluateLeaf(c, signal)),
            ConditionOperator.Or  => condition.Conditions.Any(c => EvaluateLeaf(c, signal)),
            ConditionOperator.Not => !condition.Conditions.All(c => EvaluateLeaf(c, signal)),
            _                     => false
        };
    }

    private static bool EvaluateLeaf(LeafCondition leaf, SignalGeneratedEvent signal)
    {
        var actualValue = GetIndicatorValue(leaf.Indicator, signal);
        if (actualValue is null) return false;

        return leaf.Comparator switch
        {
            Comparator.GreaterThan        => actualValue.Value > leaf.Value,
            Comparator.LessThan           => actualValue.Value < leaf.Value,
            Comparator.GreaterThanOrEqual => actualValue.Value >= leaf.Value,
            Comparator.LessThanOrEqual    => actualValue.Value <= leaf.Value,
            Comparator.Equal              => actualValue.Value == leaf.Value,
            Comparator.NotEqual           => actualValue.Value != leaf.Value,
            _                             => false
        };
    }

    private static decimal? GetIndicatorValue(IndicatorType type, SignalGeneratedEvent signal) =>
        type switch
        {
            IndicatorType.Price => signal.CurrentPrice.Value,
            _                  => ParseFromSnapshot(type, signal.IndicatorSnapshot)
        };

    private static decimal? ParseFromSnapshot(IndicatorType type, string snapshot)
    {
        // snapshot format: "RSI(14)=28.5000 | EMA(12)=50100.0000"
        var prefix = type.ToString();
        var parts  = snapshot.Split('|', StringSplitOptions.TrimEntries);

        foreach (var part in parts)
        {
            if (!part.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                continue;

            var eqIdx = part.IndexOf('=');
            if (eqIdx < 0) continue;

            if (decimal.TryParse(part[(eqIdx + 1)..], out var value))
                return value;
        }

        return null;
    }

    private static Result<Order?, DomainError> CreateOrderFromAction(
        TradingStrategy strategy,
        RuleAction      action,
        OrderSide       direction,
        Price           currentPrice)
    {
        var side = action.Type switch
        {
            ActionType.BuyMarket  => OrderSide.Buy,
            ActionType.BuyLimit   => OrderSide.Buy,
            ActionType.SellMarket => OrderSide.Sell,
            ActionType.SellLimit  => OrderSide.Sell,
            _                     => direction
        };

        var orderType = action.Type switch
        {
            ActionType.BuyLimit  => OrderType.Limit,
            ActionType.SellLimit => OrderType.Limit,
            _                    => OrderType.Market
        };

        var quantity = Quantity.Create(action.AmountUsdt / currentPrice.Value);
        if (quantity.IsFailure)
            return Result<Order?, DomainError>.Failure(quantity.Error);

        Price? limitPrice = null;
        if (orderType == OrderType.Limit && action.LimitPriceOffsetPercent is not null)
        {
            var offset    = currentPrice.Value * (action.LimitPriceOffsetPercent.Value / 100m);
            var priceResult = Price.Create(currentPrice.Value + offset);
            if (priceResult.IsFailure)
                return Result<Order?, DomainError>.Failure(priceResult.Error);
            limitPrice = priceResult.Value;
        }

        var orderResult = Order.Create(
            strategy.Id, strategy.Symbol, side, orderType,
            quantity.Value, strategy.Mode, limitPrice);

        return orderResult.Match<Result<Order?, DomainError>>(
            order => Result<Order?, DomainError>.Success(order),
            error => Result<Order?, DomainError>.Failure(error));
    }
}
