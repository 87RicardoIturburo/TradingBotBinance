namespace TradingBot.API.Dtos;

/// <summary>
/// Plantillas de estrategias predefinidas basadas en estrategias de trading ampliamente usadas.
/// Cada plantilla incluye indicadores, reglas de entrada/salida y configuración de riesgo conservadora.
/// </summary>
public static class StrategyTemplates
{
    public static IReadOnlyList<StrategyTemplateDto> All { get; } =
    [
        RsiOversoldOverbought(),
        MacdCrossover(),
        BollingerBandsBounce(),
        EmaCrossover()
    ];

    /// <summary>
    /// RSI Oversold/Overbought — Estrategia clásica de reversión a la media.
    /// Compra cuando RSI &lt; 30 (sobreventa), vende cuando RSI &gt; 70 (sobrecompra).
    /// Popular para mercados laterales y consolidación.
    /// </summary>
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

    /// <summary>
    /// MACD Crossover — Seguimiento de tendencia.
    /// Compra cuando MACD cruza por encima de 0 (momentum alcista),
    /// vende cuando cruza por debajo de 0 (momentum bajista).
    /// Ampliamente usada en trending markets.
    /// </summary>
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

    /// <summary>
    /// Bollinger Bands Bounce — Volatilidad y reversión.
    /// Compra cuando el precio cae cerca de la banda inferior (sobreventa por volatilidad),
    /// combinado con RSI bajo para confirmar. Vende cuando RSI sube.
    /// </summary>
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

    /// <summary>
    /// EMA Crossover — Seguimiento de tendencia con medias móviles.
    /// Usa EMA rápida (9) y EMA lenta (21). Compra cuando precio está por encima
    /// de EMA rápida (tendencia alcista), vende cuando cae por debajo.
    /// Estrategia simple y popular entre traders de cripto.
    /// </summary>
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
}

// ── Template DTOs ─────────────────────────────────────────────────────────

public sealed record StrategyTemplateDto(
    string                       Id,
    string                       Name,
    string                       Description,
    string                       Symbol,
    List<TemplateIndicatorDto>   Indicators,
    List<TemplateRuleDto>        Rules,
    TemplateRiskConfigDto        RiskConfig);

public sealed record TemplateIndicatorDto(
    string                      Type,
    Dictionary<string, decimal> Parameters);

public sealed record TemplateRuleDto(
    string                          Name,
    string                          RuleType,
    string                          Operator,
    List<TemplateConditionDto>      Conditions,
    string                          ActionType,
    decimal                         AmountUsdt);

public sealed record TemplateConditionDto(
    string  Indicator,
    string  Comparator,
    decimal Value);

public sealed record TemplateRiskConfigDto(
    decimal MaxOrderAmountUsdt,
    decimal MaxDailyLossUsdt,
    decimal StopLossPercent,
    decimal TakeProfitPercent,
    int     MaxOpenPositions);
