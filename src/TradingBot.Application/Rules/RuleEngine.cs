using System.Globalization;
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
        CancellationToken cancellationToken = default,
        decimal?        atrValue = null,
        string?         indicatorSnapshot = null)
    {
        var risk = strategy.RiskConfig;

        // Stop-loss: dinámico (ATR) o porcentual según configuración
        if (risk.UseAtrSizing && atrValue is > 0)
        {
            // Stop-loss dinámico: stopDistance = ATR × multiplier
            // Si trailing stop está habilitado, calcula desde el peak price en vez del entry price
            var stopDistance = atrValue.Value * risk.AtrMultiplier;
            decimal basePrice;
            if (risk.UseTrailingStop)
            {
                basePrice = position.Side == OrderSide.Buy
                    ? position.HighestPriceSinceEntry.Value
                    : position.LowestPriceSinceEntry.Value;
            }
            else
            {
                basePrice = position.EntryPrice.Value;
            }

            var stopLossPrice = position.Side == OrderSide.Buy
                ? basePrice - stopDistance
                : basePrice + stopDistance;

            var triggered = position.Side == OrderSide.Buy
                ? currentPrice.Value <= stopLossPrice
                : currentPrice.Value >= stopLossPrice;

            if (triggered)
            {
                _logger.LogWarning(
                    "Stop-loss ATR activado para posición {PositionId}: precio {Price} cruzó SL {StopLoss:F2} (ATR={Atr:F4} × {Mult}, base={BasePrice:F2}, trailing={IsTrailing})",
                    position.Id, currentPrice.Value, stopLossPrice, atrValue.Value, risk.AtrMultiplier, basePrice, risk.UseTrailingStop);

                var exitSide = position.Side == OrderSide.Buy ? OrderSide.Sell : OrderSide.Buy;
                var orderResult = Order.Create(
                    strategy.Id, position.Symbol, exitSide,
                    OrderType.Market, position.Quantity, strategy.Mode,
                    estimatedPrice: currentPrice);

                return Task.FromResult(
                    orderResult.Match<Result<Order?, DomainError>>(
                        order => Result<Order?, DomainError>.Success(order),
                        error => Result<Order?, DomainError>.Failure(error)));
            }
        }
        else
        {
            // Stop-loss porcentual clásico
            var pnlPercent = position.UnrealizedPnLPercent;
            if (pnlPercent <= -(decimal)risk.StopLossPercent)
            {
                _logger.LogWarning(
                    "Stop-loss activado para posición {PositionId}: PnL {PnL:F2}% <= -{StopLoss:F2}%",
                    position.Id, pnlPercent, risk.StopLossPercent.Value);

                var exitSide = position.Side == OrderSide.Buy ? OrderSide.Sell : OrderSide.Buy;
                var orderResult = Order.Create(
                    strategy.Id, position.Symbol, exitSide,
                    OrderType.Market, position.Quantity, strategy.Mode,
                    estimatedPrice: currentPrice);

                return Task.FromResult(
                    orderResult.Match<Result<Order?, DomainError>>(
                        order => Result<Order?, DomainError>.Success(order),
                        error => Result<Order?, DomainError>.Failure(error)));
            }
        }

        // Trailing stop: protege ganancias no realizadas ajustando el stop al peak price
        if (risk.UseTrailingStop && risk.TrailingStopPercent > 0)
        {
            var peakPrice = position.Side == OrderSide.Buy
                ? position.HighestPriceSinceEntry.Value
                : position.LowestPriceSinceEntry.Value;

            // Solo activar trailing si la posición está en ganancia
            var isInProfit = position.Side == OrderSide.Buy
                ? peakPrice > position.EntryPrice.Value
                : peakPrice < position.EntryPrice.Value;

            if (isInProfit)
            {
                var trailingStopPrice = position.Side == OrderSide.Buy
                    ? peakPrice * (1m - risk.TrailingStopPercent / 100m)
                    : peakPrice * (1m + risk.TrailingStopPercent / 100m);

                var trailingTriggered = position.Side == OrderSide.Buy
                    ? currentPrice.Value <= trailingStopPrice
                    : currentPrice.Value >= trailingStopPrice;

                if (trailingTriggered)
                {
                    _logger.LogWarning(
                        "Trailing stop activado para posición {PositionId}: precio {Price} cruzó trailing SL {TrailSL:F2} " +
                        "(peak={Peak:F2}, trail%={TrailPct}%)",
                        position.Id, currentPrice.Value, trailingStopPrice, peakPrice, risk.TrailingStopPercent);

                    var exitSide = position.Side == OrderSide.Buy ? OrderSide.Sell : OrderSide.Buy;
                    var orderResult = Order.Create(
                        strategy.Id, position.Symbol, exitSide,
                        OrderType.Market, position.Quantity, strategy.Mode,
                        estimatedPrice: currentPrice);

                    return Task.FromResult(
                        orderResult.Match<Result<Order?, DomainError>>(
                            order => Result<Order?, DomainError>.Success(order),
                            error => Result<Order?, DomainError>.Failure(error)));
                }
            }
        }

        // Take-profit automático (siempre porcentual)
        var takeProfitPnl = position.UnrealizedPnLPercent;
        if (takeProfitPnl >= (decimal)risk.TakeProfitPercent)
        {
            _logger.LogInformation(
                "Take-profit activado para posición {PositionId}: PnL {PnL:F2}% >= {TakeProfit:F2}%",
                position.Id, takeProfitPnl, risk.TakeProfitPercent.Value);

            var exitSide = position.Side == OrderSide.Buy ? OrderSide.Sell : OrderSide.Buy;
            var orderResult = Order.Create(
                strategy.Id, position.Symbol, exitSide,
                OrderType.Market, position.Quantity, strategy.Mode,
                estimatedPrice: currentPrice);

            return Task.FromResult(
                orderResult.Match<Result<Order?, DomainError>>(
                    order => Result<Order?, DomainError>.Success(order),
                    error => Result<Order?, DomainError>.Failure(error)));
        }

        // Evaluar reglas de salida configuradas.
        // Usa el snapshot de indicadores reales para evaluar condiciones como RSI > 65.
        // Sin snapshot, las condiciones de indicadores siempre fallan (snapshot incompleto).
        var exitRules = strategy.Rules
            .Where(r => r.IsEnabled && r.Type is RuleType.Exit)
            .ToList();

        var snapshot = indicatorSnapshot
            ?? $"Price={currentPrice.Value:F4}|PnL={takeProfitPnl:F2}%";

        foreach (var rule in exitRules)
        {
            var signal = new SignalGeneratedEvent(
                strategy.Id, position.Symbol,
                position.Side == OrderSide.Buy ? OrderSide.Sell : OrderSide.Buy,
                currentPrice, snapshot);

            if (!EvaluateCondition(rule.Condition, signal))
                continue;

            _logger.LogInformation(
                "Regla de salida '{RuleName}' activada para posición {PositionId} (snapshot: {Snapshot})",
                rule.Name, position.Id, snapshot);

            var exitSide = position.Side == OrderSide.Buy ? OrderSide.Sell : OrderSide.Buy;
            var orderResult = Order.Create(
                strategy.Id, position.Symbol, exitSide,
                OrderType.Market, position.Quantity, strategy.Mode,
                estimatedPrice: currentPrice);

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

    /// <summary>Tolerancia para comparaciones de igualdad entre decimales calculados.</summary>
    private const decimal EqualityEpsilon = 0.0001m;

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
            Comparator.Equal              => Math.Abs(actualValue.Value - leaf.Value) < EqualityEpsilon,
            Comparator.NotEqual           => Math.Abs(actualValue.Value - leaf.Value) >= EqualityEpsilon,
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

            if (decimal.TryParse(part[(eqIdx + 1)..],
                    NumberStyles.Number, CultureInfo.InvariantCulture, out var value))
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
            quantity.Value, strategy.Mode, limitPrice,
            estimatedPrice: orderType == OrderType.Market ? currentPrice : null);

        return orderResult.Match<Result<Order?, DomainError>>(
            order => Result<Order?, DomainError>.Success(order),
            error => Result<Order?, DomainError>.Failure(error));
    }
}
