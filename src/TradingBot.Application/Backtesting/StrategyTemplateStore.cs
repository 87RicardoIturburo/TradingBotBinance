namespace TradingBot.Application.Backtesting;

/// <summary>
/// Almacén de plantillas de estrategia. Fuente única de verdad para
/// todos los templates predefinidos. Usado por el Ranker y expuesto por la API.
/// </summary>
public static class StrategyTemplateStore
{
    public static IReadOnlyList<StrategyTemplateDto> All { get; } =
    [
        RsiOversoldOverbought(),
        MacdCrossover(),
        BollingerBandsBounce(),
        EmaCrossover(),
        TrendRiderAlcista(),
        DefensiveBottomCatcherBajista(),
        RangeScalperLateral(),
        MomentumSwingTrader(),
        AggressiveTrendCatcher(),
        QuickScalper15m()
    ];

    private static StrategyTemplateDto RsiOversoldOverbought() => new(
        Id: "rsi-oversold-overbought",
        Name: "RSI Oversold/Overbought",
        Description: "Compra en sobreventa (RSI < 30), vende en sobrecompra (RSI > 70). " +
                     "Ideal para mercados laterales. Estrategia conservadora de reversión a la media.",
        Symbol: "BTCUSDT",
        Indicators:
        [
            new("RSI", new() { ["period"] = 14, ["overbought"] = 70, ["oversold"] = 30 })
        ],
        Rules:
        [
            new("Buy RSI Oversold", "Entry", "And",
                [new("RSI", "LessThan", 30)],
                "BuyMarket", 50m),
            new("Sell RSI Overbought", "Exit", "And",
                [new("RSI", "GreaterThan", 70)],
                "SellMarket", 50m)
        ],
        RiskConfig: new(100m, 500m, 2m, 4m, 3));

    private static StrategyTemplateDto MacdCrossover() => new(
        Id: "macd-crossover",
        Name: "MACD Crossover",
        Description: "Compra cuando MACD > 0 (momentum alcista), vende cuando MACD < 0 (momentum bajista). " +
                     "Efectiva en mercados con tendencia. Configuración estándar 12/26/9.",
        Symbol: "BTCUSDT",
        Indicators:
        [
            new("MACD", new() { ["fastPeriod"] = 12, ["slowPeriod"] = 26, ["signalPeriod"] = 9 })
        ],
        Rules:
        [
            new("Buy MACD Bullish", "Entry", "And",
                [new("MACD", "GreaterThan", 0)],
                "BuyMarket", 50m),
            new("Sell MACD Bearish", "Exit", "And",
                [new("MACD", "LessThan", 0)],
                "SellMarket", 50m)
        ],
        RiskConfig: new(100m, 500m, 3m, 5m, 2));

    private static StrategyTemplateDto BollingerBandsBounce() => new(
        Id: "bollinger-bounce",
        Name: "Bollinger Bands + RSI Bounce",
        Description: "Compra cuando RSI < 35 cerca de la banda inferior de Bollinger, " +
                     "vende cuando RSI > 65. Combina volatilidad con momentum para confirmación.",
        Symbol: "ETHUSDT",
        Indicators:
        [
            new("BollingerBands", new() { ["period"] = 20, ["stdDev"] = 2 }),
            new("RSI", new() { ["period"] = 14, ["overbought"] = 65, ["oversold"] = 35 })
        ],
        Rules:
        [
            new("Buy BB+RSI Oversold", "Entry", "And",
                [new("RSI", "LessThan", 35)],
                "BuyMarket", 50m),
            new("Sell BB+RSI Overbought", "Exit", "And",
                [new("RSI", "GreaterThan", 65)],
                "SellMarket", 50m)
        ],
        RiskConfig: new(75m, 400m, 2.5m, 5m, 3));

    private static StrategyTemplateDto EmaCrossover() => new(
        Id: "ema-crossover",
        Name: "EMA Crossover (9/21)",
        Description: "Compra cuando precio > EMA(9) y RSI < 60 (tendencia alcista con espacio), " +
                     "vende cuando RSI > 70. Combina tendencia con momentum para evitar compras tardías.",
        Symbol: "BTCUSDT",
        Indicators:
        [
            new("EMA", new() { ["period"] = 9 }),
            new("RSI", new() { ["period"] = 14, ["overbought"] = 70, ["oversold"] = 30 })
        ],
        Rules:
        [
            new("Buy EMA Trend + RSI", "Entry", "And",
                [
                    new("RSI", "LessThan", 60),
                    new("RSI", "GreaterThan", 30)
                ],
                "BuyMarket", 50m),
            new("Sell RSI High", "Exit", "And",
                [new("RSI", "GreaterThan", 70)],
                "SellMarket", 50m)
        ],
        RiskConfig: new(100m, 500m, 2m, 4m, 3));

    private static StrategyTemplateDto TrendRiderAlcista() => new(
        Id: "trend-rider-alcista",
        Name: "📗 Alcista — Trend Rider",
        Description: "Monta tendencias alcistas comprando en pullbacks. ADX > 25 confirma tendencia, " +
                     "EMA(9) > EMA(21) confirma dirección. RSI 35-65 filtra zonas de entrada. " +
                     "SL ATR×2 con trailing stop. TP escalonado 2.5% → 5% → 8%. R:R 1:2.5.",
        Symbol: "BTCUSDT",
        Indicators:
        [
            new("ADX", new() { ["period"] = 14 }),
            new("EMA", new() { ["period"] = 21, ["crossoverPeriod"] = 9 }),
            new("MACD", new() { ["fastPeriod"] = 12, ["slowPeriod"] = 26, ["signalPeriod"] = 9, ["minHistogramStrength"] = 0.5m }),
            new("ATR", new() { ["period"] = 14 }),
            new("RSI", new() { ["period"] = 14, ["oversold"] = 40, ["overbought"] = 80, ["mode"] = 0 }),
            new("Volume", new() { ["period"] = 20 }),
            new("BollingerBands", new() { ["period"] = 20, ["stdDev"] = 2 })
        ],
        Rules:
        [
            new("Buy Pullback Alcista", "Entry", "And",
                [
                    new("RSI", "GreaterThan", 35),
                    new("RSI", "LessThan", 65),
                    new("ADX", "GreaterThan", 25)
                ],
                "BuyMarket", 100m),
            new("Sell Sobreextendido", "Exit", "And",
                [new("RSI", "GreaterThan", 80)],
                "SellMarket", 100m)
        ],
        RiskConfig: new(200m, 500m, 3m, 8m, 2,
            UseAtrSizing: true, RiskPercentPerTrade: 1m, AtrMultiplier: 2m,
            Timeframe: "OneHour", ConfirmationTimeframe: "FourHours",
            UseTrailingStop: true, TrailingStopPercent: 2m,
            MinConfirmationPercent: 40m, SignalCooldownPercent: 30m,
            TakeProfit1Percent: 2.5m, TakeProfit1ClosePercent: 40m,
            TakeProfit2Percent: 5m, TakeProfit2ClosePercent: 50m,
            ExitOnRegimeChange: true));

    private static StrategyTemplateDto DefensiveBottomCatcherBajista() => new(
        Id: "defensive-bottom-catcher-bajista",
        Name: "📕 Bajista — Bottom Catcher",
        Description: "Detecta fondos en mercado bajista (solo Long). RSI < 25 como señal de capitulación, " +
                     "BB(2.5σ) confirma extensión extrema, volumen ≥ 2× promedio indica selling climax. " +
                     "Risk reducido (0.5% por trade), SL ATR×1, TP escalonado 2% → 4% → 6%. Alta selectividad.",
        Symbol: "BTCUSDT",
        Indicators:
        [
            new("ADX", new() { ["period"] = 14 }),
            new("RSI", new() { ["period"] = 14, ["oversold"] = 25, ["overbought"] = 65, ["mode"] = 1 }),
            new("MACD", new() { ["fastPeriod"] = 12, ["slowPeriod"] = 26, ["signalPeriod"] = 9, ["minHistogramStrength"] = 0 }),
            new("BollingerBands", new() { ["period"] = 20, ["stdDev"] = 2.5m }),
            new("ATR", new() { ["period"] = 14 }),
            new("Volume", new() { ["period"] = 20 }),
            new("Fibonacci", new() { ["period"] = 50 })
        ],
        Rules:
        [
            new("Buy Capitulación", "Entry", "And",
                [
                    new("RSI", "LessThan", 25),
                    new("ADX", "GreaterThan", 25)
                ],
                "BuyMarket", 50m),
            new("Sell Recuperación", "Exit", "And",
                [new("RSI", "GreaterThan", 65)],
                "SellMarket", 50m)
        ],
        RiskConfig: new(100m, 250m, 1.5m, 6m, 1,
            UseAtrSizing: true, RiskPercentPerTrade: 0.5m, AtrMultiplier: 1m,
            Timeframe: "OneHour", ConfirmationTimeframe: "FourHours",
            UseTrailingStop: true, TrailingStopPercent: 1.5m,
            MinConfirmationPercent: 40m,
            TakeProfit1Percent: 2m, TakeProfit1ClosePercent: 50m,
            TakeProfit2Percent: 4m, TakeProfit2ClosePercent: 60m));

    private static StrategyTemplateDto RangeScalperLateral() => new(
        Id: "range-scalper-lateral",
        Name: "📘 Lateral — Range Scalper",
        Description: "Compra en soporte (BB inferior) y vende en resistencia (BB superior) en mercados laterales. " +
                     "ADX < 20 confirma ausencia de tendencia. RSI 30-70 define zonas de entrada/salida. " +
                     "SL fijo 1.5%, TP escalonado 1.5% → 2.5% → 3%. Win rate esperado 55-65%.",
        Symbol: "ETHUSDT",
        Indicators:
        [
            new("ADX", new() { ["period"] = 14 }),
            new("BollingerBands", new() { ["period"] = 20, ["stdDev"] = 2 }),
            new("RSI", new() { ["period"] = 14, ["oversold"] = 30, ["overbought"] = 70, ["mode"] = 0, ["confirmationZone"] = 10 }),
            new("LinearRegression", new() { ["period"] = 20, ["minRSquared"] = 0.3m }),
            new("Fibonacci", new() { ["period"] = 50 }),
            new("Volume", new() { ["period"] = 20 }),
            new("ATR", new() { ["period"] = 14 })
        ],
        Rules:
        [
            new("Buy Soporte Rango", "Entry", "And",
                [
                    new("RSI", "LessThan", 35),
                    new("ADX", "LessThan", 20)
                ],
                "BuyMarket", 75m),
            new("Sell Resistencia Rango", "Exit", "And",
                [new("RSI", "GreaterThan", 70)],
                "SellMarket", 75m)
        ],
        RiskConfig: new(150m, 400m, 1.5m, 3m, 2,
            UseAtrSizing: false, RiskPercentPerTrade: 1m, AtrMultiplier: 1.5m,
            Timeframe: "OneHour",
            MinConfirmationPercent: 35m, SignalCooldownPercent: 30m,
            TakeProfit1Percent: 1.5m, TakeProfit1ClosePercent: 50m,
            TakeProfit2Percent: 2.5m, TakeProfit2ClosePercent: 60m));

    /// <summary>
    /// Template optimizado para capital bajo ($100-300). Usa MACD + EMA cruce como generadores
    /// con solo RSI como confirmador (MinConfirmation 30%). Trailing stop protege ganancias.
    /// TP escalonado asegura parciales temprano. Sin filtro HTF para más señales.
    /// </summary>
    private static StrategyTemplateDto MomentumSwingTrader() => new(
        Id: "momentum-swing-trader",
        Name: "⚡ Momentum Swing Trader (Capital Bajo)",
        Description: "Diseñado para capital $100-300. Captura swings de momentum con MACD + EMA cruce. " +
                     "Confirmación reducida (30%) para más operaciones. Trailing stop 1.5% protege ganancias. " +
                     "TP escalonado 1.5% → 3% → 5%. Sin filtro HTF. R:R 1:2.",
        Symbol: "BTCUSDT",
        Indicators:
        [
            new("EMA", new() { ["period"] = 21, ["crossoverPeriod"] = 9 }),
            new("MACD", new() { ["fastPeriod"] = 12, ["slowPeriod"] = 26, ["signalPeriod"] = 9, ["minHistogramStrength"] = 0 }),
            new("RSI", new() { ["period"] = 14, ["oversold"] = 35, ["overbought"] = 70, ["mode"] = 0 }),
            new("ATR", new() { ["period"] = 14 })
        ],
        Rules:
        [
            new("Buy Momentum", "Entry", "And",
                [
                    new("RSI", "GreaterThan", 35),
                    new("RSI", "LessThan", 65)
                ],
                "BuyMarket", 150m),
            new("Sell Overbought", "Exit", "And",
                [new("RSI", "GreaterThan", 72)],
                "SellMarket", 150m)
        ],
        RiskConfig: new(150m, 300m, 2.5m, 5m, 2,
            UseAtrSizing: true, RiskPercentPerTrade: 2m, AtrMultiplier: 1.5m,
            Timeframe: "OneHour",
            UseTrailingStop: true, TrailingStopPercent: 1.5m,
            MinConfirmationPercent: 30m, SignalCooldownPercent: 20m,
            TakeProfit1Percent: 1.5m, TakeProfit1ClosePercent: 40m,
            TakeProfit2Percent: 3m, TakeProfit2ClosePercent: 50m));

    /// <summary>
    /// Template agresivo de tendencia para capital bajo. Solo 3 indicadores (ADX + EMA + ATR).
    /// Risk 2% por trade. Trailing stop ATR dinámico. Sin confirmación HTF ni BTC.
    /// Mayor frecuencia de trades para compensar el capital reducido.
    /// </summary>
    private static StrategyTemplateDto AggressiveTrendCatcher() => new(
        Id: "aggressive-trend-catcher",
        Name: "🔥 Aggressive Trend Catcher (Capital Bajo)",
        Description: "Máxima exposición a tendencias para capital $100-300. Solo 3 indicadores (ADX + EMA + ATR) " +
                     "para señales rápidas. Risk 2% por trade, ATR×1.5 como SL. Trailing stop ATR dinámico. " +
                     "Sin filtro de confirmación HTF ni BTC. TP escalonado 2% → 4% → 7%. Operaciones frecuentes.",
        Symbol: "BTCUSDT",
        Indicators:
        [
            new("ADX", new() { ["period"] = 14 }),
            new("EMA", new() { ["period"] = 21, ["crossoverPeriod"] = 9 }),
            new("ATR", new() { ["period"] = 14 })
        ],
        Rules:
        [
            new("Buy Trend Entry", "Entry", "And",
                [new("ADX", "GreaterThan", 22)],
                "BuyMarket", 150m),
            new("Sell Trend Weakening", "Exit", "And",
                [new("ADX", "LessThan", 15)],
                "SellMarket", 150m)
        ],
        RiskConfig: new(200m, 400m, 2.5m, 7m, 2,
            UseAtrSizing: true, RiskPercentPerTrade: 2m, AtrMultiplier: 1.5m,
            Timeframe: "OneHour",
            UseTrailingStop: true, TrailingStopPercent: 2m,
            MinConfirmationPercent: 0m, SignalCooldownPercent: 15m,
            TakeProfit1Percent: 2m, TakeProfit1ClosePercent: 35m,
            TakeProfit2Percent: 4m, TakeProfit2ClosePercent: 50m,
            ExitOnRegimeChange: true));

    /// <summary>
    /// Scalper en timeframe de 15 minutos para capital bajo. Operaciones rápidas con BB + RSI.
    /// TP pequeño (1% → 2%) pero más frecuente. MaxPositionDuration 8 velas (2 horas) para no quedar atrapado.
    /// </summary>
    private static StrategyTemplateDto QuickScalper15m() => new(
        Id: "quick-scalper-15m",
        Name: "⏱️ Quick Scalper 15min (Capital Bajo)",
        Description: "Scalping en 15 minutos para capital $100-300. BB + RSI detectan reversiones rápidas. " +
                     "TP 1% → 2% con duración máxima 8 velas (2h). Más operaciones, ganancias pequeñas consistentes. " +
                     "SL 1.2% ajustado. Ideal para mercados laterales con micro-oscilaciones.",
        Symbol: "BTCUSDT",
        Indicators:
        [
            new("BollingerBands", new() { ["period"] = 20, ["stdDev"] = 2 }),
            new("RSI", new() { ["period"] = 10, ["oversold"] = 28, ["overbought"] = 72, ["mode"] = 0 }),
            new("ATR", new() { ["period"] = 14 })
        ],
        Rules:
        [
            new("Buy BB Lower + RSI", "Entry", "And",
                [new("RSI", "LessThan", 32)],
                "BuyMarket", 120m),
            new("Sell BB Upper + RSI", "Exit", "And",
                [new("RSI", "GreaterThan", 70)],
                "SellMarket", 120m)
        ],
        RiskConfig: new(150m, 200m, 1.2m, 2m, 3,
            UseAtrSizing: false, RiskPercentPerTrade: 1.5m, AtrMultiplier: 1m,
            Timeframe: "FifteenMinutes",
            MinConfirmationPercent: 0m, SignalCooldownPercent: 10m,
            TakeProfit1Percent: 0.8m, TakeProfit1ClosePercent: 50m,
            TakeProfit2Percent: 1.5m, TakeProfit2ClosePercent: 60m,
            MaxPositionDurationCandles: 8));
}
