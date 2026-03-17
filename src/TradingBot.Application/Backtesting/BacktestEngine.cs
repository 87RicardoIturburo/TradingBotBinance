using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TradingBot.Application.RiskManagement;
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
/// Aplica fees y slippage configurables para resultados realistas.
/// </summary>
internal sealed class BacktestEngine
{
    private readonly TradingFeeConfig _feeConfig;
    private readonly ILogger<BacktestEngine> _logger;

    public BacktestEngine(IOptions<TradingFeeConfig> feeConfig, ILogger<BacktestEngine> logger)
    {
        _feeConfig = feeConfig.Value;
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
        decimal totalFees = 0m;
        decimal totalSlippage = 0m;
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

            // 1. Procesar tick → señal? (actualiza indicadores internamente)
            var signalResult = await tradingStrategy.ProcessTickAsync(tick, cancellationToken);
            // No se interrumpe aquí: la evaluación de salida debe ejecutarse siempre
            // que haya una posición abierta, independientemente del resultado de la señal.

            // 2. Si hay posición abierta → delegar la evaluación de salida al RuleEngine (fuente única de verdad)
            if (openPosition is not null)
            {
                openPosition = openPosition with
                {
                    CurrentPrice              = kline.Close,
                    HighestPriceSinceEntry   = Math.Max(openPosition.HighestPriceSinceEntry, kline.Close),
                    LowestPriceSinceEntry    = Math.Min(openPosition.LowestPriceSinceEntry,  kline.Close)
                };

                var fakePosition = Position.Open(
                    strategy.Id, strategy.Symbol, openPosition.Side,
                    Price.Create(openPosition.EntryPrice).Value,
                    Quantity.Create(openPosition.Quantity).Value);
                // Reproducir los peaks históricos para que el trailing stop
                // se calcule sobre el precio máximo/mínimo real desde la apertura.
                if (openPosition.HighestPriceSinceEntry > openPosition.EntryPrice)
                    fakePosition.UpdatePrice(Price.Create(openPosition.HighestPriceSinceEntry).Value);
                if (openPosition.LowestPriceSinceEntry < openPosition.EntryPrice)
                    fakePosition.UpdatePrice(Price.Create(openPosition.LowestPriceSinceEntry).Value);
                fakePosition.UpdatePrice(price.Value);

                var currentSnapshot = tradingStrategy.GetCurrentSnapshot();
                var exitResult = await ruleEngine.EvaluateExitRulesAsync(
                    strategy, fakePosition, price.Value, cancellationToken,
                    atrValue: tradingStrategy.CurrentAtrValue,
                    indicatorSnapshot: currentSnapshot);

                // Si EvaluateExitRulesAsync falla (p. ej. Order.Create rechazó la orden),
                // usar el check directo de BacktestPosition como red de seguridad.
                bool shouldExit;
                string exitReason;

                if (exitResult.IsFailure)
                {
                    _logger.LogWarning(
                        "EvaluateExitRulesAsync falló para posición abierta: {Error}. Aplicando SL/TP directo.",
                        exitResult.Error.Message);

                    var pnlPct = openPosition.UnrealizedPnLPercent;
                    shouldExit = pnlPct <= -(decimal)strategy.RiskConfig.StopLossPercent
                              || pnlPct >=  (decimal)strategy.RiskConfig.TakeProfitPercent;
                    exitReason = DetermineExitReason(openPosition, strategy);
                }
                else
                {
                    shouldExit = exitResult.Value is not null;
                    exitReason = shouldExit ? DetermineExitReason(openPosition, strategy) : string.Empty;
                }

                if (shouldExit)
                {
                    var trade = ClosePosition(openPosition, kline.Close, kline.OpenTime, exitReason, _feeConfig);
                    trades.Add(trade);
                    realizedPnL += trade.NetPnL;
                    totalFees += trade.Fees;
                    totalSlippage += trade.SlippageCost;

                    var priceChangePct = openPosition.EntryPrice > 0
                        ? (kline.Close - openPosition.EntryPrice) / openPosition.EntryPrice * 100m
                        : 0m;
                    var durationMinutes = (kline.OpenTime - openPosition.OpenedAt).TotalMinutes;

                    _logger.LogInformation(
                        "Backtest [{Name}] ■ Trade #{N} '{Reason}' | "
                        + "{Entry:F2}→{Exit:F2} ({PricePct:+0.00;-0.00;0.00}%;PnL%={PnLPct:+0.00;-0.00;0.00}%) | NetPnL={PnL:F4} USDT | "
                        + "SL={SL}% TP={TP}% | Dur={Dur:F0}min | [{Snapshot}]",
                        strategy.Name, trades.Count, exitReason,
                        openPosition.EntryPrice, kline.Close,
                        priceChangePct, openPosition.UnrealizedPnLPercent,
                        trade.NetPnL,
                        strategy.RiskConfig.StopLossPercent.Value,
                        strategy.RiskConfig.TakeProfitPercent.Value,
                        durationMinutes,
                        currentSnapshot);

                    openPosition = null;
                }
            }

            // 3. Si hay señal válida y no hay posición abierta → evaluar entrada
            if (signalResult.IsSuccess && signalResult.Value is { } signal && openPosition is null)
            {
                // Circuit breaker diario: no abrir nuevas posiciones si se
                // alcanzó el límite de pérdida diaria de esta estrategia
                var dailyPnL = GetDailyPnL(trades, kline.OpenTime);
                var dailyLimitHit = strategy.RiskConfig.MaxDailyLossUsdt > 0
                    && dailyPnL <= -strategy.RiskConfig.MaxDailyLossUsdt;

                if (!dailyLimitHit)
                {
                    var orderResult = await ruleEngine.EvaluateAsync(strategy, signal, cancellationToken);
                    if (orderResult.IsSuccess && orderResult.Value is { } order)
                    {
                        // Aplicar maxOrderAmountUsdt como cap de tamaño de posición.
                        // Si UseAtrSizing está habilitado, calcular con PositionSizer.
                        var qty = order.Quantity.Value;

                        if (strategy.RiskConfig.UseAtrSizing && signal.AtrValue is > 0 && kline.Close > 0)
                        {
                            // Simular balance como capital inicial (totalInvested cubre lo ya invertido)
                            var simulatedBalance = strategy.RiskConfig.MaxOrderAmountUsdt * 10m;
                            var sizing = PositionSizer.Calculate(
                                simulatedBalance,
                                strategy.RiskConfig.RiskPercentPerTrade / 100m,
                                signal.AtrValue.Value,
                                strategy.RiskConfig.AtrMultiplier,
                                kline.Close,
                                strategy.RiskConfig.MaxOrderAmountUsdt);
                            qty = sizing.QuantityBaseAsset;
                        }
                        else if (kline.Close > 0)
                        {
                            var rawQty  = qty;
                            var cappedQty = strategy.RiskConfig.MaxOrderAmountUsdt / kline.Close;
                            qty = Math.Min(rawQty, cappedQty);

                            // Advertir si la regla tenía un amountUsdt mucho mayor que el MaxOrderAmountUsdt
                            // porque puede significar una mala configuración de la estrategia.
                            var ruleAmountUsdt = rawQty * kline.Close;
                            if (ruleAmountUsdt > strategy.RiskConfig.MaxOrderAmountUsdt * 1.01m)
                                _logger.LogWarning(
                                    "Backtest [{Name}] amountUsdt de la regla ({RuleAmount:F2} USDT) " +
                                    "supera MaxOrderAmountUsdt ({MaxAmount:F2} USDT). " +
                                    "La cantidad fue limitada de {RawQty:F6} a {CappedQty:F6} {Symbol}. " +
                                    "Verificá el amountUsdt de la regla de entrada en la configuración.",
                                    strategy.Name, ruleAmountUsdt,
                                    strategy.RiskConfig.MaxOrderAmountUsdt,
                                    rawQty, qty, strategy.Symbol.Value);
                        }

                        if (qty > 0)
                        {
                            var entryPrice = FeeAndSlippageCalculator.ApplySlippage(
                                kline.Close, order.Side, _feeConfig.SlippagePercent);
                            totalInvested += entryPrice * qty;
                            openPosition = new BacktestPosition(
                                order.Side, entryPrice, entryPrice, qty, kline.OpenTime,
                                HighestPriceSinceEntry: entryPrice,
                                LowestPriceSinceEntry:  entryPrice);

                            var slPrice = order.Side == OrderSide.Buy
                                ? entryPrice * (1m - strategy.RiskConfig.StopLossPercent.AsDecimalFactor)
                                : entryPrice * (1m + strategy.RiskConfig.StopLossPercent.AsDecimalFactor);

                            _logger.LogInformation(
                                "Backtest [{Name}] ▶ Trade #{N} ENTRADA {Side} | "
                                + "Market={Market:F2} Efectivo={Entry:F2} | Qty={Qty:F6} | "
                                + "SL esperado={SL:F2} ({SLPct}%) | Fees={FeeRate:P3} | [{Snapshot}]",
                                strategy.Name, trades.Count + 1, order.Side,
                                kline.Close, entryPrice, qty,
                                slPrice, strategy.RiskConfig.StopLossPercent.Value,
                                _feeConfig.EffectiveTakerFee,
                                signal.IndicatorSnapshot);
                        }
                    }
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
            var trade = ClosePosition(openPosition, lastKline.Close, lastKline.OpenTime, "Fin del backtest", _feeConfig);
            trades.Add(trade);
            realizedPnL += trade.NetPnL;
            totalFees += trade.Fees;
            totalSlippage += trade.SlippageCost;
        }

        var wins = trades.Count(t => t.NetPnL > 0);
        var losses = trades.Count(t => t.NetPnL < 0);
        var grossPnL = trades.Sum(t => t.GrossPnL);

        var roi = totalInvested > 0 ? realizedPnL / totalInvested * 100m : 0m;
        var metrics = BacktestMetrics.Calculate(trades);

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
            GrossPnL: grossPnL,
            TotalFeesUsdt: totalFees,
            TotalSlippageUsdt: totalSlippage,
            TotalPnL: realizedPnL,
            TotalInvested: totalInvested,
            ReturnOnInvestment: roi,
            MaxDrawdownPercent: maxDrawdown,
            AveragePnLPerTrade: trades.Count > 0 ? realizedPnL / trades.Count : 0m,
            BestTrade: trades.Count > 0 ? trades.Max(t => t.NetPnL) : 0m,
            WorstTrade: trades.Count > 0 ? trades.Min(t => t.NetPnL) : 0m,
            Metrics: metrics,
            Trades: trades,
            EquityCurve: equityCurve);

        var exitSummary = trades.Count > 0
            ? string.Join(" | ", trades
                .GroupBy(t => t.ExitReason)
                .Select(g => $"{g.Key}×{g.Count()}(avg={g.Average(t => t.NetPnL):F4} USDT)"))
            : "sin trades";

        _logger.LogInformation(
            "Backtest completado: {Trades} trades, P&L={PnL:N2} USDT, Win rate={WinRate:N1}% | {ExitSummary}",
            trades.Count, realizedPnL, result.WinRate, exitSummary);

        return result;
    }

    /// <summary>
    /// Calcula el P&amp;L realizado en el día UTC del timestamp dado.
    /// Usado como circuit breaker de pérdida diaria en el backtest.
    /// </summary>
    private static decimal GetDailyPnL(List<BacktestTrade> closedTrades, DateTimeOffset currentTime)
    {
        var dayStart = new DateTimeOffset(currentTime.UtcDateTime.Date, TimeSpan.Zero);
        var dayEnd   = dayStart.AddDays(1);
        return closedTrades
            .Where(t => t.ExitTime >= dayStart && t.ExitTime < dayEnd)
            .Sum(t => t.NetPnL);
    }

    private static BacktestTrade ClosePosition(
        BacktestPosition position, decimal exitPrice, DateTimeOffset exitTime,
        string reason, TradingFeeConfig feeConfig)
    {
        // position.EntryPrice ya tiene el slippage de entrada aplicado al abrir la posición.
        // Solo aplicamos slippage al precio de salida para evitar conteo doble.
        var exitSide     = position.Side == OrderSide.Buy ? OrderSide.Sell : OrderSide.Buy;
        var adjustedExit = FeeAndSlippageCalculator.ApplySlippage(
            exitPrice, exitSide, feeConfig.SlippagePercent);

        var grossPnL = position.Side == OrderSide.Buy
            ? (adjustedExit - position.EntryPrice) * position.Quantity
            : (position.EntryPrice - adjustedExit) * position.Quantity;

        var entryFee   = FeeAndSlippageCalculator.CalculateFee(
            position.EntryPrice, position.Quantity, feeConfig.EffectiveTakerFee);
        var exitFee    = FeeAndSlippageCalculator.CalculateFee(
            adjustedExit, position.Quantity, feeConfig.EffectiveTakerFee);
        var totalFees  = entryFee + exitFee;
        var slippage   = Math.Abs((exitPrice - adjustedExit) * position.Quantity);
        var netPnL     = grossPnL - totalFees;

        return new BacktestTrade(
            position.Side,
            position.EntryPrice,
            exitPrice,
            position.Quantity,
            grossPnL,
            totalFees,
            slippage,
            netPnL,
            position.OpenedAt,
            exitTime,
            reason);
    }

    /// <summary>
    /// Infiere la razón de salida a partir del P&amp;L y el estado de la posición al momento del cierre.
    /// Usado exclusivamente para el etiquetado de resultados de backtest.
    /// </summary>
    private static string DetermineExitReason(BacktestPosition position, TradingStrategy strategy)
    {
        var pnlPercent = position.UnrealizedPnLPercent;
        // Solo etiquetar como stop-loss si se alcanzó el umbral configurado.
        // Un PnL negativo pequeño puede ser una salida por regla (RSI > 65) con precio
        // ligeramente por debajo del entry — eso es "Exit rule", no "Stop-loss".
        if (pnlPercent <= -(decimal)strategy.RiskConfig.StopLossPercent)
            return "Stop-loss";
        if (pnlPercent >= (decimal)strategy.RiskConfig.TakeProfitPercent)
            return "Take-profit";

        // Detectar trailing stop: la posición estaba en ganancia y el precio
        // retrocedió desde el pico histórico hasta el umbral de trailing stop.
        var risk = strategy.RiskConfig;
        if (risk.UseTrailingStop && risk.TrailingStopPercent > 0)
        {
            if (position.Side == OrderSide.Buy
                && position.HighestPriceSinceEntry > position.EntryPrice
                && position.CurrentPrice <= position.HighestPriceSinceEntry * (1m - risk.TrailingStopPercent / 100m))
            {
                return "Trailing stop";
            }
            if (position.Side == OrderSide.Sell
                && position.LowestPriceSinceEntry < position.EntryPrice
                && position.CurrentPrice >= position.LowestPriceSinceEntry * (1m + risk.TrailingStopPercent / 100m))
            {
                return "Trailing stop";
            }
        }

        return "Exit rule";
    }
}

// ── Records ───────────────────────────────────────────────────────────────

internal sealed record BacktestPosition(
    OrderSide Side,
    decimal EntryPrice,
    decimal CurrentPrice,
    decimal Quantity,
    DateTimeOffset OpenedAt,
    decimal HighestPriceSinceEntry,
    decimal LowestPriceSinceEntry)
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
    decimal                      GrossPnL,
    decimal                      TotalFeesUsdt,
    decimal                      TotalSlippageUsdt,
    decimal                      TotalPnL,
    decimal                      TotalInvested,
    decimal                      ReturnOnInvestment,
    decimal                      MaxDrawdownPercent,
    decimal                      AveragePnLPerTrade,
    decimal                      BestTrade,
    decimal                      WorstTrade,
    BacktestMetrics              Metrics,
    IReadOnlyList<BacktestTrade> Trades,
    IReadOnlyList<EquityPoint>   EquityCurve);

/// <summary>Trade individual ejecutado durante el backtest.</summary>
public sealed record BacktestTrade(
    OrderSide      Side,
    decimal        EntryPrice,
    decimal        ExitPrice,
    decimal        Quantity,
    decimal        GrossPnL,
    decimal        Fees,
    decimal        SlippageCost,
    decimal        NetPnL,
    DateTimeOffset EntryTime,
    DateTimeOffset ExitTime,
    string         ExitReason);

/// <summary>Punto de la curva de equity para graficar.</summary>
public sealed record EquityPoint(
    DateTimeOffset Timestamp,
    decimal        Equity);
