using Microsoft.Extensions.Logging;
using TradingBot.Core.Common;
using TradingBot.Core.Entities;
using TradingBot.Core.Enums;
using TradingBot.Core.Events;
using TradingBot.Core.Interfaces.Services;
using TradingBot.Core.Interfaces.Trading;
using TradingBot.Core.ValueObjects;

namespace TradingBot.Application.Backtesting;

/// <summary>
/// Motor de backtesting. Recorre velas históricas en memoria y simula
/// la ejecución completa de una estrategia sin tocar la base de datos.
/// </summary>
internal sealed class BacktestEngine
{
    private readonly ILogger<BacktestEngine> _logger;

    public BacktestEngine(ILogger<BacktestEngine> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Ejecuta un backtest completo con los datos históricos proporcionados.
    /// Todo se procesa en memoria — no persiste órdenes ni posiciones.
    /// </summary>
    public async Task<BacktestResult> RunAsync(
        TradingStrategy strategy,
        ITradingStrategy tradingStrategy,
        IRuleEngine ruleEngine,
        IReadOnlyList<Kline> klines,
        CancellationToken cancellationToken = default)
    {
        var trades = new List<BacktestTrade>();
        var equityCurve = new List<EquityPoint>();
        var openPosition = (BacktestPosition?)null;
        decimal realizedPnL = 0m;
        decimal totalInvested = 0m;
        decimal peakEquity = 0m;
        decimal maxDrawdown = 0m;

        _logger.LogInformation(
            "Iniciando backtest de '{Name}' con {Count} velas",
            strategy.Name, klines.Count);

        for (var i = 0; i < klines.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var kline = klines[i];
            var price = Price.Create(kline.Close);
            if (price.IsFailure) continue;

            var tick = new MarketTickReceivedEvent(
                strategy.Symbol,
                price.Value, price.Value, price.Value,
                kline.Volume, kline.OpenTime);

            // 1. Procesar tick → señal?
            var signalResult = await tradingStrategy.ProcessTickAsync(tick, cancellationToken);
            if (signalResult.IsFailure) continue;

            // 2. Si hay posición abierta → evaluar salida
            if (openPosition is not null)
            {
                openPosition = openPosition with { CurrentPrice = kline.Close };

                // Stop-loss / take-profit
                var pnlPercent = openPosition.UnrealizedPnLPercent;
                var shouldExit = false;
                var exitReason = "";

                if (pnlPercent <= -(decimal)strategy.RiskConfig.StopLossPercent)
                {
                    shouldExit = true;
                    exitReason = "Stop-loss";
                }
                else if (pnlPercent >= (decimal)strategy.RiskConfig.TakeProfitPercent)
                {
                    shouldExit = true;
                    exitReason = "Take-profit";
                }

                // Evaluar reglas de salida configuradas
                if (!shouldExit)
                {
                    var fakePosition = Position.Open(
                        strategy.Id, strategy.Symbol, openPosition.Side,
                        Price.Create(openPosition.EntryPrice).Value,
                        Quantity.Create(openPosition.Quantity).Value);
                    fakePosition.UpdatePrice(price.Value);

                    var exitResult = await ruleEngine.EvaluateExitRulesAsync(
                        strategy, fakePosition, price.Value, cancellationToken);

                    if (exitResult.IsSuccess && exitResult.Value is not null)
                    {
                        shouldExit = true;
                        exitReason = "Exit rule";
                    }
                }

                if (shouldExit)
                {
                    var trade = ClosePosition(openPosition, kline.Close, kline.OpenTime, exitReason);
                    trades.Add(trade);
                    realizedPnL += trade.PnL;
                    openPosition = null;
                }
            }

            // 3. Si hay señal y no hay posición abierta → evaluar entrada
            if (signalResult.Value is { } signal && openPosition is null)
            {
                var orderResult = await ruleEngine.EvaluateAsync(strategy, signal, cancellationToken);
                if (orderResult.IsSuccess && orderResult.Value is { } order)
                {
                    var qty = order.Quantity.Value;
                    totalInvested += kline.Close * qty;
                    openPosition = new BacktestPosition(
                        order.Side, kline.Close, kline.Close, qty, kline.OpenTime);
                }
            }

            // 4. Calcular equity y drawdown
            var unrealized = openPosition?.UnrealizedPnL ?? 0m;
            var totalEquity = realizedPnL + unrealized;

            if (totalEquity > peakEquity)
                peakEquity = totalEquity;

            var currentDrawdown = peakEquity > 0
                ? (peakEquity - totalEquity) / peakEquity * 100m
                : 0m;

            if (currentDrawdown > maxDrawdown)
                maxDrawdown = currentDrawdown;

            // Registrar punto de equity cada 60 velas (~1 hora en 1m)
            if (i % 60 == 0 || i == klines.Count - 1)
            {
                equityCurve.Add(new EquityPoint(kline.OpenTime, totalEquity));
            }
        }

        // Cerrar posición abierta al final del backtest
        if (openPosition is not null)
        {
            var lastKline = klines[^1];
            var trade = ClosePosition(openPosition, lastKline.Close, lastKline.OpenTime, "Fin del backtest");
            trades.Add(trade);
            realizedPnL += trade.PnL;
        }

        var wins = trades.Count(t => t.PnL > 0);
        var losses = trades.Count(t => t.PnL < 0);

        var roi = totalInvested > 0 ? realizedPnL / totalInvested * 100m : 0m;

        var result = new BacktestResult(
            StrategyName: strategy.Name,
            Symbol: strategy.Symbol.Value,
            From: klines[0].OpenTime,
            To: klines[^1].OpenTime,
            TotalKlines: klines.Count,
            TotalTrades: trades.Count,
            WinningTrades: wins,
            LosingTrades: losses,
            WinRate: trades.Count > 0 ? (decimal)wins / trades.Count * 100m : 0m,
            TotalPnL: realizedPnL,
            TotalInvested: totalInvested,
            ReturnOnInvestment: roi,
            MaxDrawdownPercent: maxDrawdown,
            AveragePnLPerTrade: trades.Count > 0 ? realizedPnL / trades.Count : 0m,
            BestTrade: trades.Count > 0 ? trades.Max(t => t.PnL) : 0m,
            WorstTrade: trades.Count > 0 ? trades.Min(t => t.PnL) : 0m,
            Trades: trades,
            EquityCurve: equityCurve);

        _logger.LogInformation(
            "Backtest completado: {Trades} trades, P&L={PnL:N2} USDT, Win rate={WinRate:N1}%",
            trades.Count, realizedPnL, result.WinRate);

        return result;
    }

    private static BacktestTrade ClosePosition(
        BacktestPosition position, decimal exitPrice, DateTimeOffset exitTime, string reason)
    {
        var pnl = position.Side == OrderSide.Buy
            ? (exitPrice - position.EntryPrice) * position.Quantity
            : (position.EntryPrice - exitPrice) * position.Quantity;

        return new BacktestTrade(
            position.Side,
            position.EntryPrice,
            exitPrice,
            position.Quantity,
            pnl,
            position.OpenedAt,
            exitTime,
            reason);
    }
}

// ── Records ───────────────────────────────────────────────────────────────

internal sealed record BacktestPosition(
    OrderSide Side,
    decimal EntryPrice,
    decimal CurrentPrice,
    decimal Quantity,
    DateTimeOffset OpenedAt)
{
    public decimal UnrealizedPnL => Side == OrderSide.Buy
        ? (CurrentPrice - EntryPrice) * Quantity
        : (EntryPrice - CurrentPrice) * Quantity;

    public decimal UnrealizedPnLPercent =>
        EntryPrice == 0m ? 0m
        : UnrealizedPnL / (EntryPrice * Quantity) * 100m;
}

/// <summary>Resultado completo de un backtest.</summary>
public sealed record BacktestResult(
    string                       StrategyName,
    string                       Symbol,
    DateTimeOffset               From,
    DateTimeOffset               To,
    int                          TotalKlines,
    int                          TotalTrades,
    int                          WinningTrades,
    int                          LosingTrades,
    decimal                      WinRate,
    decimal                      TotalPnL,
    decimal                      TotalInvested,
    decimal                      ReturnOnInvestment,
    decimal                      MaxDrawdownPercent,
    decimal                      AveragePnLPerTrade,
    decimal                      BestTrade,
    decimal                      WorstTrade,
    IReadOnlyList<BacktestTrade> Trades,
    IReadOnlyList<EquityPoint>   EquityCurve);

/// <summary>Trade individual ejecutado durante el backtest.</summary>
public sealed record BacktestTrade(
    OrderSide      Side,
    decimal        EntryPrice,
    decimal        ExitPrice,
    decimal        Quantity,
    decimal        PnL,
    DateTimeOffset EntryTime,
    DateTimeOffset ExitTime,
    string         ExitReason);

/// <summary>Punto de la curva de equity para graficar.</summary>
public sealed record EquityPoint(
    DateTimeOffset Timestamp,
    decimal        Equity);
