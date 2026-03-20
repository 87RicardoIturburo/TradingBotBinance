# Auditoría de Producción #3 — TradingBot

> **Fecha:** Julio 2025  
> **Alcance:** Flujo completo Wizard → Scanner → Ranker → Backtest → Creación → Activación → AutoPilot → RiskBudget  
> **Objetivo:** Certificar que el bot es seguro para operar con dinero real  
> **Estado:** ✅ COMPLETADA — 11 hallazgos corregidos, 344 tests pasan

---

## 📋 Contexto para la conversación que retome

Este documento detalla **11 problemas encontrados** en la lógica de negocio del bot de trading.
Los hallazgos están ordenados por **prioridad de corrección**. Cada uno incluye:
- Archivo y línea exacta del problema
- Código actual problemático
- Impacto en producción (pérdida financiera)
- Solución propuesta con pseudocódigo

**Archivos clave que debes conocer antes de empezar:**

| Archivo | Rol |
|---------|-----|
| `Application/Wizard/RunSetupWizardCommand.cs` | Wizard: crea estrategias automáticamente |
| `Application/AutoPilot/AutoPilotWorker.cs` | Background service que evalúa regímenes cada 5 min |
| `Application/AutoPilot/StrategyRotatorService.cs` | Lógica de rotación de estrategias por régimen |
| `Application/Backtesting/RunTemplateRankingCommand.cs` | Ranker: corre backtest de 7 templates |
| `Application/RiskManagement/RiskBudgetService.cs` | Guardián de capital global |
| `Application/RiskManagement/RiskBudgetConfig.cs` | Config del guardián |
| `Application/Strategies/StrategyEngine.cs` | Motor principal (línea 1382: `GetIndicatorWarmUpPeriod`) |
| `Application/Strategies/Indicators/AdxIndicator.cs` | ADX con `IsBullish`/`IsBearish` (línea 48-51) |
| `Core/Interfaces/Services/IStrategyEngine.cs` | Record `StrategyEngineStatus` (no tiene +DI/-DI) |

---

## 🔴 HALLAZGO 1 — Wizard activa estrategias con backtest negativo

**Severidad:** 🔴 CRÍTICO  
**Riesgo financiero:** ALTO — el bot opera estrategias que su propio backtest dice que pierden dinero  
**Archivo:** `src/TradingBot.Application/Wizard/RunSetupWizardCommand.cs`  
**Línea:** 93-106

### Código actual (problemático)

```csharp
// Línea 93-96: toma el primer resultado SIN verificar si es rentable
if (rankingResult.IsFailure || rankingResult.Value.Rankings.Count == 0)
    continue;

var bestTemplate = rankingResult.Value.Rankings[0]; // ← Puede tener Sharpe=-2.0, PnL=-$500
```

Después en línea 102 crea y activa la estrategia sin ningún filtro de calidad:

```csharp
var created = await CreateStrategyFromTemplateAsync(
    template, symbolScore.Symbol, mode, capitalPerSymbol, cancellationToken);

if (created is not null)
    createdStrategies.Add(created);  // ← Se activa aunque el backtest sea desastroso
```

### Impacto

En un mercado bajista, todos los templates pueden tener backtest negativo. El Wizard igual
selecciona el "menos malo" (ej: Sharpe = -0.3, PnL = -$80) y lo pone a operar con dinero real.

### Solución propuesta

Después de obtener el `bestTemplate` (línea 96), agregar validación:

```csharp
var bestTemplate = rankingResult.Value.Rankings[0];

// AUDIT-1: No crear estrategia si el backtest es negativo
if (bestTemplate.SharpeRatio < 0.3m || bestTemplate.TotalPnL <= 0 || bestTemplate.TotalTrades < 5)
{
    logger.LogWarning(
        "Template '{Name}' descartado para {Symbol}: Sharpe={Sharpe:F2}, PnL={PnL:F2}, Trades={Trades}",
        bestTemplate.TemplateName, symbolScore.Symbol,
        bestTemplate.SharpeRatio, bestTemplate.TotalPnL, bestTemplate.TotalTrades);
    continue;
}
```

**Umbrales sugeridos:**
- `SharpeRatio >= 0.3` — mínimo aceptable para trading real
- `TotalPnL > 0` — no operar algo que pierde en backtest
- `TotalTrades >= 5` — suficientes trades para ser estadísticamente significativo

---

## 🔴 HALLAZGO 2 — AutoPilot hardcodea `isBullish = true`

**Severidad:** 🔴 CRÍTICO  
**Riesgo financiero:** ALTO — en mercado bajista el bot activa estrategia alcista (compra mientras cae)  
**Archivo:** `src/TradingBot.Application/AutoPilot/AutoPilotWorker.cs`  
**Línea:** 68-70

### Código actual (problemático)

```csharp
var isBullish = true; // ← HARDCODEADO, SIEMPRE true
var result = await rotator.EvaluateRotationAsync(
    status.Symbol.Value, status.CurrentRegime, isBullish, stoppingToken);
```

### Cómo afecta al rotador

En `StrategyRotatorService.cs`, línea 97-104:

```csharp
private string? SelectTemplate(MarketRegime regime, bool isBullish) => regime switch
{
    MarketRegime.Trending when isBullish  => _config.TrendingTemplateId,   // ← SIEMPRE este
    MarketRegime.Trending when !isBullish => _config.BearishTemplateId,    // ← NUNCA se ejecuta
    MarketRegime.Ranging                  => _config.RangingTemplateId,
    MarketRegime.HighVolatility           => null,
    _                                     => null
};
```

### Impacto

El template "Defensive Bottom Catcher Bajista" (`BearishTemplateId`) **nunca se activa**.
En un bear market, el AutoPilot activa "Trend Rider Alcista" que compra en tendencia bajista.

### Solución propuesta (2 pasos)

**Paso A:** Agregar `IsBullish` al record `StrategyEngineStatus`.

Archivo: `src/TradingBot.Core/Interfaces/Services/IStrategyEngine.cs`

El record actual:
```csharp
public sealed record StrategyEngineStatus(
    Guid           StrategyId,
    string         StrategyName,
    Symbol         Symbol,
    bool           IsProcessing,
    DateTimeOffset LastTickAt,
    int            TicksProcessed,
    int            SignalsGenerated,
    int            OrdersPlaced,
    MarketRegime   CurrentRegime = MarketRegime.Unknown);
```

Agregar un campo `bool IsBullish`:
```csharp
public sealed record StrategyEngineStatus(
    Guid           StrategyId,
    string         StrategyName,
    Symbol         Symbol,
    bool           IsProcessing,
    DateTimeOffset LastTickAt,
    int            TicksProcessed,
    int            SignalsGenerated,
    int            OrdersPlaced,
    MarketRegime   CurrentRegime = MarketRegime.Unknown,
    bool           IsBullish = true);
```

**Paso B:** Propagar `IsBullish` desde el ADX del indicador.

En `StrategyEngine.cs`, el método `ToStatus()` está en la clase `StrategyRunnerState` (línea 1463):
```csharp
public StrategyEngineStatus ToStatus() => new(
    StrategyId, StrategyName, Symbol,
    IsProcessing, LastTickAt,
    TicksProcessed, SignalsGenerated, OrdersPlaced,
    Strategy.CurrentRegime);
```

Se necesita obtener `IsBullish` de la estrategia. `ITradingStrategy` no expone `IsBullish`, pero
`DefaultTradingStrategy` accede internamente al `AdxIndicator` que sí tiene `IsBullish` (línea 48).

Opción más limpia: agregar propiedad a `ITradingStrategy`:
```csharp
// En ITradingStrategy:
bool IsBullish { get; }

// En DefaultTradingStrategy:
public bool IsBullish =>
    _indicators.TryGetValue(IndicatorType.ADX, out var adx)
    && adx is AdxIndicator { IsReady: true, IsBullish: true };
```

Luego en `ToStatus()`:
```csharp
public StrategyEngineStatus ToStatus() => new(
    StrategyId, StrategyName, Symbol,
    IsProcessing, LastTickAt,
    TicksProcessed, SignalsGenerated, OrdersPlaced,
    Strategy.CurrentRegime,
    Strategy.IsBullish);
```

**Paso C:** Usar el valor real en `AutoPilotWorker.cs` (línea 68):
```csharp
var isBullish = status.IsBullish; // En vez de true
```

**Nota:** Verificar que los DTOs del frontend (`StrategyEngineStatusDto`) también se actualicen
para incluir `IsBullish` si el frontend lo necesita.

---

## 🔴 HALLAZGO 3 — AutoPilot NO corre backtest antes de activar

**Severidad:** 🔴 CRÍTICO  
**Riesgo financiero:** MEDIO — activa templates sin verificar rentabilidad en el symbol actual  
**Archivo:** `src/TradingBot.Application/AutoPilot/StrategyRotatorService.cs`  
**Línea:** 128-223 (método `ActivateTemplateAsync`)

### Código actual (problemático)

```csharp
private async Task<string?> ActivateTemplateAsync(string symbol, string templateId, CancellationToken ct)
{
    var template = StrategyTemplateStore.All.FirstOrDefault(t =>
        t.Id.Equals(templateId, StringComparison.OrdinalIgnoreCase));
    // ...
    // Crea la estrategia directamente del template y la activa
    // SIN correr ningún backtest para verificar que sea rentable en este symbol
    var createResult = await _configService.CreateAsync(strategy, ct);
    var activateResult = await _configService.ActivateAsync(createResult.Value.Id, ct);
}
```

### Impacto

El rotador puede activar "Range Scalper" en DOGEUSDT donde esa estrategia pierde dinero,
porque nunca verificó con un backtest rápido.

### Solución propuesta

Inyectar `ISender` (MediatR) y ejecutar un backtest rápido (7 días) antes de activar:

```csharp
// Antes de crear la estrategia:
var rankingResult = await _mediator.Send(
    new RunTemplateRankingCommand(symbol, FromDays: 7), ct);

if (rankingResult.IsSuccess)
{
    var templateRank = rankingResult.Value.Rankings
        .FirstOrDefault(r => r.TemplateId == templateId);

    if (templateRank is null || templateRank.SharpeRatio < 0 || templateRank.TotalPnL <= 0)
    {
        _logger.LogWarning(
            "AutoPilot: template '{TemplateId}' no rentable para {Symbol}, rotación cancelada",
            templateId, symbol);
        return null;
    }
}
```

**Nota:** Esto agrega latencia a la rotación (~2-5s por el backtest). Considerar si es aceptable
o si se debe cachear el resultado del último scan/ranking.

---

## 🔴 HALLAZGO 4 — Wizard NO aplica SymbolProfile al crear estrategia

**Severidad:** 🔴 CRÍTICO  
**Riesgo financiero:** MEDIO — umbrales de volatilidad incorrectos para altcoins  
**Archivo:** `src/TradingBot.Application/Wizard/RunSetupWizardCommand.cs`  
**Línea:** 143-151

### Código actual (problemático)

```csharp
// El Ranker ya calculó un SymbolProfile con umbrales adaptados, PERO
// el Wizard usa los valores CRUDOS del template:
var riskResult = Core.ValueObjects.RiskConfig.Create(
    maxOrder,
    template.RiskConfig.MaxDailyLossUsdt,       // ← Sin adaptar
    template.RiskConfig.StopLossPercent,          // ← OK (universal)
    template.RiskConfig.TakeProfitPercent,        // ← OK (universal)
    template.RiskConfig.MaxOpenPositions,          // ← OK (universal)
    template.RiskConfig.UseAtrSizing,
    template.RiskConfig.RiskPercentPerTrade,
    template.RiskConfig.AtrMultiplier);
    // ← FALTA: highVolatilityBandWidthPercent, highVolatilityAtrPercent, maxSpreadPercent
```

### Cómo el Ranker SÍ lo hace bien

En `RunTemplateRankingCommand.cs`, línea 243-258:

```csharp
private static Result<RiskConfig, DomainError> BuildAdaptedRiskConfig(
    StrategyTemplateRiskConfigDto tplRisk,
    SymbolProfile profile)
{
    return RiskConfig.Create(
        // ...
        highVolatilityBandWidthPercent: profile.AdjustedHighVolatilityBandWidthPercent,
        highVolatilityAtrPercent: profile.AdjustedHighVolatilityAtrPercent,
        maxSpreadPercent: profile.AdjustedMaxSpreadPercent);
}
```

### Impacto

Ejemplo: Template calibrado para BTC tiene `HighVolatilityAtrPercent = 3%`.
DOGEUSDT tiene ATR% normal de 6%. El filtro bloquea TODAS las señales como "HighVolatility"
y la estrategia nunca opera. O al revés: en un symbol muy estable, no filtra volatilidad real.

### Solución propuesta

El `rankingResult` ya contiene el `SymbolProfile` calculado. Usarlo al crear la estrategia:

```csharp
// Después de línea 96 (var bestTemplate = rankingResult.Value.Rankings[0]):
var profile = rankingResult.Value.Profile;

// En CreateStrategyFromTemplateAsync, cambiar RiskConfig.Create para usar profile:
var riskResult = Core.ValueObjects.RiskConfig.Create(
    maxOrder,
    template.RiskConfig.MaxDailyLossUsdt,
    template.RiskConfig.StopLossPercent,
    template.RiskConfig.TakeProfitPercent,
    template.RiskConfig.MaxOpenPositions,
    template.RiskConfig.UseAtrSizing,
    template.RiskConfig.RiskPercentPerTrade,
    template.RiskConfig.AtrMultiplier,
    highVolatilityBandWidthPercent: profile.AdjustedHighVolatilityBandWidthPercent,
    highVolatilityAtrPercent: profile.AdjustedHighVolatilityAtrPercent,
    maxSpreadPercent: profile.AdjustedMaxSpreadPercent);
```

Esto requiere pasar el `SymbolProfile` al método `CreateStrategyFromTemplateAsync`
(cambiar su firma para aceptar un parámetro adicional `SymbolProfile profile`).

---

## 🔴 HALLAZGO 5 — AutoPilot hardcodea `TradingMode.PaperTrading`

**Severidad:** 🔴 CRÍTICO  
**Riesgo financiero:** MEDIO — las rotaciones nunca ejecutan órdenes reales  
**Archivo:** `src/TradingBot.Application/AutoPilot/StrategyRotatorService.cs`  
**Línea:** 174

### Código actual (problemático)

```csharp
var strategyResult = Core.Entities.TradingStrategy.Create(
    strategyName, symbolResult.Value, TradingMode.PaperTrading, // ← HARDCODEADO
    riskResult.Value, $"Creada por AutoPilot desde template {template.Name}",
    timeframe, confirmationTf);
```

### Impacto

Si el usuario configuró el bot para Live trading, el AutoPilot crea todas las estrategias
rotadas en PaperTrading. Las señales se generan, las "órdenes" se simulan, pero nunca se
ejecuta una orden real en Binance. El usuario cree que está operando pero no lo está.

### Solución propuesta

Agregar `TradingMode` a `AutoPilotConfig`:

```csharp
// En AutoPilotConfig.cs:
public string DefaultTradingMode { get; set; } = "PaperTrading";
```

Luego en `ActivateTemplateAsync`:
```csharp
var mode = Enum.TryParse<TradingMode>(_config.DefaultTradingMode, true, out var m)
    ? m : TradingMode.PaperTrading;

var strategyResult = Core.Entities.TradingStrategy.Create(
    strategyName, symbolResult.Value, mode, // ← Usa la config
    riskResult.Value, ...);
```

**Alternativa más segura:** Detectar el modo de la estrategia que se está desactivando y usar el mismo:

```csharp
// En DeactivateCurrentAsync, además de retornar el nombre, retornar el modo:
// Cambiar return type a (string? Name, TradingMode Mode)
```

---

## 🟡 HALLAZGO 6 — RiskBudget no se inicializa al arrancar el motor

**Severidad:** 🟡 ALTO  
**Riesgo financiero:** MEDIO — ventana de tiempo sin protección tras reinicio  
**Archivo:** `src/TradingBot.Application/RiskManagement/RiskBudgetService.cs`  
**Línea:** 30-31

### Código actual (problemático)

```csharp
private decimal _accumulatedLoss;    // Inicia en 0
private RiskLevel _currentLevel;      // Inicia en Normal (default de enum)
```

`RefreshAsync()` recalcula desde la DB, pero **¿quién lo llama al arrancar?**

El `StrategyEngine.ExecuteAsync` arranca los runners pero no llama `RefreshAsync` del RiskBudget.
Si una orden se procesa antes del primer Refresh, `_currentLevel = Normal` y pasa sin protección,
aunque la pérdida acumulada real sea de $49 (debería ser `CloseOnly`).

### Solución propuesta

En `StrategyEngine.ExecuteAsync`, antes de cargar estrategias:

```csharp
protected override async Task ExecuteAsync(CancellationToken stoppingToken)
{
    // AUDIT-6: Inicializar RiskBudget antes de arrancar estrategias
    using (var scope = _scopeFactory.CreateScope())
    {
        var riskBudget = scope.ServiceProvider.GetRequiredService<IRiskBudget>();
        await riskBudget.RefreshAsync(stoppingToken);
    }

    await LoadWithRetryAsync(stoppingToken);
    // ...
}
```

**Nota:** `IRiskBudget` está registrado como singleton, así que el scope solo es para resolver.
Verificar el ciclo de vida real de `RiskBudgetService` en DI.

---

## 🟡 HALLAZGO 7 — RiskBudget lee TODA la historia de posiciones

**Severidad:** 🟡 ALTO  
**Riesgo financiero:** BAJO (performance, no financiero directo)  
**Archivo:** `src/TradingBot.Application/RiskManagement/RiskBudgetService.cs`  
**Línea:** 113-126

### Código actual (problemático)

```csharp
private async Task<decimal> CalculateTotalPnLAsync(CancellationToken cancellationToken)
{
    var closedPositions = await _positionRepository.GetClosedByDateRangeAsync(
        DateTimeOffset.MinValue, DateTimeOffset.UtcNow, cancellationToken);
        // ← MinValue = lee TODA la historia desde el inicio de los tiempos

    var realizedPnL = closedPositions
        .Where(p => p.RealizedPnL.HasValue)
        .Sum(p => p.RealizedPnL!.Value);

    var openPositions = await _positionRepository.GetOpenPositionsAsync(cancellationToken);
    var unrealizedPnL = openPositions.Sum(p => p.UnrealizedPnL);

    return realizedPnL + unrealizedPnL;
}
```

### Impacto

Después de meses operando con miles de posiciones, esta query se vuelve costosa.
Se ejecuta en cada `RefreshAsync` que se llama desde `RiskManager.ValidateAsync`,
es decir, **antes de cada orden**.

### Solución propuesta

Opción A — Ventana de tiempo configurable:
```csharp
var from = DateTimeOffset.UtcNow.AddDays(-30); // O desde que se configuró el capital
var closedPositions = await _positionRepository.GetClosedByDateRangeAsync(
    from, DateTimeOffset.UtcNow, cancellationToken);
```

Opción B — Agregar `BudgetStartDate` a `RiskBudgetConfig` para marcar cuándo se inició el presupuesto:
```csharp
public DateTimeOffset? BudgetStartDate { get; set; }
```

---

## 🟡 HALLAZGO 8 — Wizard acepta symbols con semáforo amarillo

**Severidad:** 🟡 ALTO  
**Riesgo financiero:** BAJO — opera en symbols de calidad media  
**Archivo:** `src/TradingBot.Application/Wizard/RunSetupWizardCommand.cs`  
**Línea:** 75-78

### Código actual (problemático)

```csharp
var topSymbols = scanResult.Value
    .Where(s => s.TrafficLight != "🔴")  // ← 🟡 pasa el filtro
    .Take(3);
```

### Impacto

Un symbol con `TrafficLight = "🟡"` (score ~40-59, spread alto, volatilidad incómoda) pasa
el filtro. El Wizard podría crear estrategias en symbols con spreads del 0.3% que comen
toda la ganancia potencial.

### Solución propuesta

```csharp
var topSymbols = scanResult.Value
    .Where(s => s.TrafficLight == "🟢")  // Solo operables
    .Take(3)
    .ToList();

// Si no hay suficientes verdes, permitir amarillos con score mínimo
if (topSymbols.Count < 2)
{
    var yellows = scanResult.Value
        .Where(s => s.TrafficLight == "🟡" && s.Score >= 55)
        .Take(3 - topSymbols.Count);
    topSymbols.AddRange(yellows);
}
```

---

## 🟡 HALLAZGO 9 — Ranker usa warm-up incorrecto para ADX

**Severidad:** 🟡 ALTO  
**Riesgo financiero:** MEDIO — resultados de backtest imprecisos, decisiones basadas en datos incorrectos  
**Archivo:** `src/TradingBot.Application/Backtesting/RunTemplateRankingCommand.cs`  
**Línea:** 207-214

### Código actual (problemático)

```csharp
var maxPeriod = strategy.Indicators
    .Select(i => (int)i.GetParameter("period", 14))
    .DefaultIfEmpty(0)
    .Max();

var warmUpCount = Math.Min(maxPeriod + 10, klines.Count);
for (var i = 0; i < warmUpCount; i++)
    tradingStrategy.WarmUpOhlc(klines[i].High, klines[i].Low, klines[i].Close, klines[i].Volume);
```

### El problema

Este código tiene **la misma lógica que acabamos de corregir** en `StrategyEngine.GetIndicatorWarmUpPeriod`:
- ADX con period=14 necesita `_count >= 28` para `IsReady`
- MACD necesita `slowPeriod + signalPeriod`
- El código solo usa `period` genérico

Con warm-up insuficiente, el ADX del Ranker nunca llega a `IsReady`.
El `MarketRegimeDetector` retorna `Unknown`, y la estrategia opera sin contexto de régimen.
**Los resultados del backtest que luego se usan para decidir qué template activar son incorrectos.**

### Solución propuesta

Extraer `GetIndicatorWarmUpPeriod` a un helper estático compartido, o duplicar la lógica:

```csharp
var maxPeriod = strategy.Indicators
    .Select(i => i.Type switch
    {
        IndicatorType.MACD => (int)i.GetParameter("slowPeriod", 26)
                            + (int)i.GetParameter("signalPeriod", 9),
        IndicatorType.ADX  => (int)i.GetParameter("period", 14) * 2,
        _                  => (int)i.GetParameter("period", 14)
    })
    .DefaultIfEmpty(0)
    .Max();
```

**Mejor aún:** Extraer `GetIndicatorWarmUpPeriod` del `StrategyEngine` a una clase helper
estática en `Application/Strategies/` que ambos (`StrategyEngine` y `RunTemplateRankingCommand`)
puedan reutilizar. Evita duplicar lógica.

**Nota:** Verificar si `RunBacktestCommand.cs` (el backtest manual) tiene el mismo problema.
Buscar en línea ~90-100 de ese archivo.

---

## 🟡 HALLAZGO 10 — AutoPilot no cierra posiciones al rotar

**Severidad:** 🟡 MEDIO  
**Riesgo financiero:** MEDIO — posiciones huérfanas sin gestión  
**Archivo:** `src/TradingBot.Application/AutoPilot/StrategyRotatorService.cs`  
**Línea:** 14 (config `ClosePositionsOnRotation`) y 114-126

### Código actual (problemático)

La config tiene:
```csharp
public bool ClosePositionsOnRotation { get; set; } = true;
```

Pero `DeactivateCurrentAsync` solo desactiva:
```csharp
private async Task<string?> DeactivateCurrentAsync(string symbol, CancellationToken ct)
{
    var allActive = await _configService.GetAllActiveAsync(ct);
    var current = allActive.FirstOrDefault(s =>
        s.Symbol.Value.Equals(symbol, StringComparison.OrdinalIgnoreCase)
        && s.Name.Contains("AutoPilot", StringComparison.OrdinalIgnoreCase));

    if (current is null)
        return null;

    await _configService.DeactivateAsync(current.Id, ct);
    // ← NO cierra posiciones abiertas de esta estrategia
    return current.Name;
}
```

### Impacto

Al rotar de "Trend Rider" a "Range Scalper", las posiciones abiertas por Trend Rider
quedan sin gestión: no se evalúan SL/TP, no se cierran, no se monitorean.

### Solución propuesta

```csharp
private async Task<string?> DeactivateCurrentAsync(string symbol, CancellationToken ct)
{
    // ... (encontrar current como ahora)

    if (_config.ClosePositionsOnRotation)
    {
        // Cerrar posiciones abiertas de la estrategia saliente
        var positionRepo = _scopeFactory.CreateScope()
            .ServiceProvider.GetRequiredService<IPositionRepository>();
        var openPositions = await positionRepo.GetOpenByStrategyIdAsync(current.Id, ct);

        if (openPositions.Count > 0)
        {
            var orderService = _scopeFactory.CreateScope()
                .ServiceProvider.GetRequiredService<IOrderService>();

            foreach (var position in openPositions)
            {
                // Crear orden de cierre a mercado
                // ... (lógica similar a EvaluateExitRulesAsync cuando cierra por SL)
            }
        }
    }

    await _configService.DeactivateAsync(current.Id, ct);
    return current.Name;
}
```

**Nota:** Esto requiere inyectar `IServiceScopeFactory` en `StrategyRotatorService`.
También considerar que cerrar posiciones puede fallar (Binance error, rate limit).

---

## 🟡 HALLAZGO 11 — StrategyRotatorService usa Dictionary no thread-safe

**Severidad:** 🟡 BAJO  
**Riesgo financiero:** BAJO — race condition teórica  
**Archivo:** `src/TradingBot.Application/AutoPilot/StrategyRotatorService.cs`  
**Línea:** 22-23

### Código actual (problemático)

```csharp
private readonly Dictionary<string, DateTimeOffset> _lastRotationAt = new(StringComparer.OrdinalIgnoreCase);
private readonly Dictionary<string, string> _activeTemplateBySymbol = new(StringComparer.OrdinalIgnoreCase);
```

### Impacto

Si `AutoPilotWorker` y el endpoint `POST /api/autopilot/evaluate` llaman a
`EvaluateRotationAsync` concurrentemente, `Dictionary` puede corromperse.

### Solución propuesta

```csharp
private readonly ConcurrentDictionary<string, DateTimeOffset> _lastRotationAt = new(StringComparer.OrdinalIgnoreCase);
private readonly ConcurrentDictionary<string, string> _activeTemplateBySymbol = new(StringComparer.OrdinalIgnoreCase);
```

---

## 📋 ORDEN DE CORRECCIÓN RECOMENDADO

> **✅ TODOS RESUELTOS** — Correcciones aplicadas en orden de prioridad.

```
Prioridad 1 ✅ COMPLETADA:
  ├── HALLAZGO 1: ✅ Filtro Sharpe ≥ 0.3, PnL > 0, Trades ≥ 2 (ajustado de 5 a 2 tras prueba real)
  ├── HALLAZGO 2: ✅ ITradingStrategy.IsBullish → StrategyEngineStatus → AutoPilotWorker
  └── HALLAZGO 9: ✅ IndicatorWarmUpHelper compartido (ADX×2, MACD slow+signal) en 5 archivos

Prioridad 2 ✅ COMPLETADA:
  ├── HALLAZGO 4: ✅ SymbolProfile del ranking pasado a CreateStrategyFromTemplateAsync
  ├── HALLAZGO 5: ✅ AutoPilotConfig.DefaultTradingMode configurable + appsettings.json
  └── HALLAZGO 6: ✅ InitializeRiskBudgetAsync en StrategyEngine.ExecuteAsync

Prioridad 3 ✅ COMPLETADA:
  ├── HALLAZGO 3:  ✅ IsTemplateProfitableAsync (backtest 7 días) antes de activar en AutoPilot
  ├── HALLAZGO 10: ✅ CloseOpenPositionsAsync con IOrderService al rotar (respeta config)
  └── HALLAZGO 8:  ✅ Prioriza 🟢, fallback a 🟡 con score ≥ 55

Prioridad 4 ✅ COMPLETADA:
  ├── HALLAZGO 7:  ✅ BudgetStartDate configurable, default últimos 30 días
  └── HALLAZGO 11: ✅ ConcurrentDictionary en StrategyRotator
```

---

## 🔧 Correcciones aplicadas — Resumen completo

### Archivos creados (1)
- `Application/Strategies/IndicatorWarmUpHelper.cs` — Helper compartido para warm-up de indicadores

### Archivos modificados (15+)

| Archivo | Hallazgo | Cambio |
|---------|:--------:|--------|
| `Wizard/RunSetupWizardCommand.cs` | #1, #4, #8 | Filtro calidad + SymbolProfile + score mínimo + FromDays: 30 |
| `AutoPilot/AutoPilotWorker.cs` | #2 | `status.IsBullish` en vez de `true` |
| `AutoPilot/StrategyRotatorService.cs` | #3, #5, #10, #11 | Backtest pre-activación + TradingMode config + cierre posiciones + ConcurrentDictionary + ISender + IPositionRepository + IOrderService |
| `AutoPilot/AutoPilotConfig.cs` | #5 | `DefaultTradingMode` propiedad |
| `Strategies/StrategyEngine.cs` | #6, #9 | `InitializeRiskBudgetAsync` + delega a `IndicatorWarmUpHelper` |
| `Strategies/IndicatorWarmUpHelper.cs` | #9 | Helper estático compartido (ADX×2, MACD slow+signal) |
| `Strategies/DefaultTradingStrategy.cs` | #2 | Propiedad `IsBullish` desde ADX |
| `Backtesting/RunTemplateRankingCommand.cs` | #9 | Usa `IndicatorWarmUpHelper` |
| `Backtesting/RunBacktestCommand.cs` | #9 | Usa `IndicatorWarmUpHelper` |
| `Backtesting/OptimizationEngine.cs` | #9 | Usa `IndicatorWarmUpHelper` (2 ocurrencias) |
| `RiskManagement/RiskBudgetService.cs` | #7 | Ventana temporal con `BudgetStartDate` |
| `RiskManagement/RiskBudgetConfig.cs` | #7 | `BudgetStartDate` propiedad |
| `Core/Interfaces/Trading/ITradingStrategy.cs` | #2 | Propiedad `IsBullish` en interfaz |
| `Core/Interfaces/Services/IStrategyEngine.cs` | #2 | `IsBullish` en `StrategyEngineStatus` |
| `API/appsettings.json` | #5 | `DefaultTradingMode` en sección `AutoPilot` |

### Ajuste post-producción
- **`TotalTrades < 5` → `TotalTrades < 2`**: El umbral original descartaba estrategias de tendencia
  con Sharpe 5.39 y PnL +$27.85 porque solo generaban 2 trades en 14 días — comportamiento normal
  para Trend Rider en timeframe 1H.
- **`FromDays: 14` → `FromDays: 30`**: Con solo 14 días, la mayoría de templates producían 0 trades.
  30 días da ~720 klines que cubren 2-3 ciclos de mercado completos.

### Verificación
- **344 tests pasan, 0 fallos**
- Compilación correcta
- Probado con datos reales de Binance Demo (log del 2026-03-20)

---

## ✅ Cosas que SÍ están bien implementadas

Para contexto — no todo es negativo. Estos componentes fueron revisados y están correctos:

1. **RiskManager.ValidateAsync** — Valida correctamente antes de cada orden
2. **Circuit Breaker global** — Drawdown checker funciona cada N segundos
3. **Warm-up del StrategyEngine** — Corregido (ADX × 2 + fallback a producción)
4. **BacktestEngine.RunAsync** — La mecánica del backtest es sólida
5. **SymbolProfiler.Analyze** — Calcula correctamente los umbrales adaptados
6. **MarketRegimeDetector.Detect** — Lógica de detección de régimen es correcta
7. **Histéresis de régimen (EST-19)** — Evita ping-pong entre regímenes
8. **Paper Trading mode** — Simulación correcta sin afectar Live
9. **Señales solo desde klines cerradas (TRADE-1)** — Evita ruido de ticks
10. **Persistencia de indicadores en Redis** — Sobrevive reinicios
