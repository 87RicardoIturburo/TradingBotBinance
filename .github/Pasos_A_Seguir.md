# TradingBot — Plan de Trabajo

> **Contexto para Copilot en un nuevo chat.**
> Leer junto con `.github/copilot-instructions.md` y `docs/PROJECT.md`.

---

## 📊 Estado actual (2026-03-17)

| Métrica | Valor |
|---------|-------|
| Build | ✅ 8 proyectos compilan |
| Tests | ✅ **376/376 passing** — Core (80) + Application (288) + Integration (8) |
| TFM | .NET 10 / C# 14 |
| Migraciones EF Core | 10 aplicadas |
| E2E | ✅ CRUD estrategias, indicadores, reglas, activar/desactivar, SignalR ticks |

### Capas completadas

| Capa | Archivos clave |
|------|----------------|
| **Core** | 11 enums, 12 VOs, 4 entidades, 10 eventos, 19 interfaces |
| **Application** | 14 MediatR handlers, StrategyEngine, RuleEngine, RiskManager, OrderService, 9 indicadores, PositionSizer, BacktestEngine, OptimizationEngine, TradingMetrics |
| **Infrastructure** | EF Core + Npgsql, 3 repos, Binance.Net WS+REST, Redis, Serilog, UserDataStreamService |
| **API** | 5 controllers (30+ endpoints), SignalR Hub, Auth, Rate Limiting, Health Checks |
| **Frontend** | 8 páginas Blazor WASM, TradingApiClient, SignalR |
| **Tests** | Core (80), Application (288), Integration (8) |

### Etapas completadas

| Etapa | Descripción |
|-------|-------------|
| 🔴 1 | Bloqueantes Binance (filtros exchange, balance, User Data Stream) |
| 🟠 2 | Simulación realista (fees, slippage, métricas Sharpe/Sortino) |
| 🟡 3 | Calidad de señales (ADX, ATR sizing, régimen de mercado) |
| 🟢 4 | Riesgo de portafolio (exposición, concentración, drawdown) |
| 🔵 5 | Ejecución Testnet (BinanceSpotOrderExecutor, UserDataStream) |
| ⚫ 6 | Observabilidad (health checks, CI/CD, auth, rate limiting, métricas) |

> Detalle de cada paso completado: ver historial de git.

---

## 🔴 BUGS CRÍTICOS RESUELTOS — Auditoría anterior

> Todos corregidos. Ver historial de git para detalles.

| BUG | Descripción | Estado |
|-----|-------------|--------|
| BUG-1 | Trailing stop no persiste peak prices | ✅ CORREGIDO |
| BUG-2 | Warm-up ignora el timeframe de la estrategia | ✅ CORREGIDO |
| BUG-3 | CheckAccountDrawdownAsync nunca se invoca | ✅ CORREGIDO |
| BUG-4 | Señales Sell pasan filtro de duplicados sin posición Long | ✅ CORREGIDO |
| BUG-5 | GlobalRiskSettings se parsea sin InvariantCulture | ✅ CORREGIDO |
| BUG-6 | Balance check falla silenciosamente en modo Live | ✅ CORREGIDO |
| BUG-7 | InvalidateCacheAsync no limpia balances por asset | ✅ CORREGIDO |

---

## 🔴 AUDITORÍA 2 — Errores críticos detectados (bloquean producción)

> Resultado de una segunda auditoría exhaustiva del código.
> Cada uno DEBE resolverse antes de operar con dinero real.

### CRIT-1: ATR usa solo Close — subestima volatilidad real

**Archivo**: `AtrIndicator.cs` (línea 48)

**Problema**: El True Range se calcula como `|close - previousClose|`. El ATR real en Binance usa `max(High-Low, |High-prevClose|, |Low-prevClose|)`. Al usar solo cierres, el ATR subestima la volatilidad real en un 30-60% en mercados con mechas largas (crypto es famoso por esto).

**Impacto en producción**:
- **PositionSizer**: `stopDistance = ATR × multiplier` es demasiado estrecho → posiciones demasiado grandes → pérdidas amplificadas
- **ATR Stop-Loss dinámico en RuleEngine**: se coloca demasiado cerca del precio → stop-outs prematuros constantes

**Solución**: El `Update(decimal value)` del `ITechnicalIndicator` solo recibe un valor. Se necesita un overload o refactor para que el ATR reciba High, Low, Close. Opciones:
1. Agregar `UpdateOhlc(decimal high, decimal low, decimal close)` al ATR y llamarlo desde `ProcessKlineAsync` (solo klines tienen OHLC)
2. En `ProcessTickAsync`, seguir con la aproximación close-only (los ticks no tienen High/Low por definición)
3. En `WarmUpIndicatorsAsync` ya se pasan klines con OHLC pero solo se usa `kline.Close` en cada indicador

**Archivos a modificar**: `AtrIndicator.cs`, `ITechnicalIndicator.cs` (agregar interfaz OHLC opcional), `DefaultTradingStrategy.ProcessKlineAsync`, `BacktestEngine.RunAsync`

---

### CRIT-2: MIN_NOTIONAL no se valida para órdenes Market

**Archivo**: `ExchangeSymbolFilters.ValidateAndAdjust` (línea 81)

**Problema**: La validación de MIN_NOTIONAL solo aplica `if (adjustedPrice.HasValue)`. Para Market orders, `limitPrice` es `null`, por lo que **nunca se valida MIN_NOTIONAL**. Binance rechaza con error `-1013` cualquier Market order cuyo `quantity × marketPrice < minNotional` (típicamente 5-10 USDT).

**Impacto**: Toda Market order con monto pequeño (ej: $3 USDT) pasa todos los filtros locales pero Binance la rechaza. En un bot con position sizing dinámico (ATR bajo → cantidad pequeña), esto ocurrirá constantemente.

**Solución**: Pasar el `estimatedPrice` (precio de mercado actual) a `ValidateAndAdjust` y usarlo como fallback cuando `limitPrice` es null:
```csharp
var priceForNotional = adjustedPrice ?? estimatedPrice;
if (MinNotional > 0 && priceForNotional.HasValue)
{
    var notional = adjustedQty * priceForNotional.Value;
    if (notional < MinNotional) → error
}
```

**Archivos a modificar**: `ExchangeSymbolFilters.ValidateAndAdjust` (agregar parámetro `estimatedPrice`), `OrderService.ValidateExchangeFiltersAsync` (pasar `order.EstimatedPrice`)

---

### CRIT-3: UserDataStream procesa order fills sin CancellationToken

**Archivo**: `UserDataStreamService.OnOrderUpdate` (línea 185)

**Problema**: El `Task.Run(async () => await ProcessOrderUpdateAsync(update))` no pasa `CancellationToken`. Durante un shutdown (`StopAsync`), la suscripción WebSocket se cancela pero los `Task.Run` en vuelo pueden:
1. Intentar escribir en la DB después de que el `DbContext` fue disposed
2. Perder un fill de Binance (la posición queda abierta en la DB pero cerrada en Binance)

**Impacto**: Órdenes Filled reportadas por Binance podrían no sincronizarse → desincronización DB ↔ Binance.

**Solución**: Pasar `_cts.Token` al `Task.Run` y al `ProcessOrderUpdateAsync`. Implementar un mecanismo de "graceful drain" que espere a que los procesadores en vuelo terminen antes de cerrar el scope.

---

### CRIT-4: Backtest usa balance ficticio arbitrario para PositionSizer

**Archivo**: `BacktestEngine.RunAsync` (línea 174)

**Problema**:
```csharp
var simulatedBalance = strategy.RiskConfig.MaxOrderAmountUsdt * 10m;
```
El PositionSizer calcula `riskAmount = balance × riskPercent`. Con un balance artificial de `MaxOrderAmount × 10`, el sizing no refleja la realidad. Si `MaxOrderAmountUsdt = 100` y `riskPercent = 1%`, el risk amount es `$10`, pero con un balance real de `$500` sería `$5`. Los resultados del backtest sobreestiman o subestiman el tamaño de posiciones reales.

**Impacto**: El backtest genera métricas (Sharpe, Sortino, WinRate) basadas en sizing irreal. Las decisiones de "esta estrategia es rentable" pueden ser incorrectas.

**Solución**: Agregar un parámetro `initialBalance` al `RunBacktestCommand`/`BacktestEngine.RunAsync` y usar ese balance reducido por las pérdidas acumuladas (equity curve) para calcular el sizing en cada trade. Esto simula el efecto real del compounding.

---

### CRIT-5: Concurrencia entre entry signals (kline) y exit evaluations (tick)

**Archivo**: `StrategyEngine.cs` — loops de klines y ticks

**Problema**: Aunque el `_strategyLocks` serializa el procesamiento dentro de cada loop, hay un escenario de race condition entre `ProcessSingleKlineAsync` y `ProcessSingleTickAsync`:
1. **Tick loop** detecta SL, crea orden Sell, posición se cierra
2. **Kline loop** (en paralelo) verificó `existingPositions` ANTES del cierre, ve posición Long, decide no abrir nueva posición Buy
3. O peor: kline genera señal Buy, crea nueva posición, Y el tick simultáneamente cierra la posición antigua → dos órdenes en vuelo

El lock por estrategia (`SemaphoreSlim(1,1)`) SÍ está implementado y serializa ambos loops. **Sin embargo**, el scope de `IOrderService` y `IPositionRepository` son distintos en cada loop. Si el tick cierra una posición y no hace `SaveChangesAsync` antes de que el kline consulte `GetOpenByStrategyIdAsync`, el kline verá datos stale de la DB.

**Impacto**: Bajo probabilidad (los semáforos mitigan), pero en periodos de alta volatilidad con velas que cierran justo cuando se dispara un SL, podría ocurrir.

**Solución verificada**: El semáforo ya está en ambos loops y es el mismo por estrategia. Los `SaveChangesAsync` ocurren dentro del lock. **Este issue es de baja probabilidad pero documentar**. Una mejora sería agregar un `HasPendingCloseOrderAsync` check antes de abrir nueva posición (ya existe en el kline loop para señales de salida, falta para entradas).

---

### CRIT-6: GlobalRisk deshabilitados por defecto — sin red de seguridad

**Archivo**: `appsettings.json` (líneas 33-40) + `GlobalRiskSettings.cs`

**Problema**: Los valores por defecto en `appsettings.json`:
```json
"MaxPortfolioLongExposureUsdt": 0,    // ← 0 = deshabilitado
"MaxExposurePerSymbolPercent": 0,      // ← 0 = deshabilitado
"MaxAccountDrawdownPercent": 0         // ← 0 = deshabilitado
```
Un usuario que arranca el bot sin configurar estos valores NO tendrá:
- Límite de exposición total del portafolio
- Límite de concentración por símbolo
- Kill switch automático por drawdown de cuenta

El `MaxDailyLossUsdt: 100` es el único freno real, pero 100 USDT puede no ser apropiado para todas las cuentas.

**Impacto**: Sin drawdown check y sin límite de exposición, una estrategia buggeada puede comprar indefinidamente hasta agotar la cuenta.

**Solución**: Cambiar los defaults a valores conservadores:
```json
"MaxPortfolioLongExposureUsdt": 500,
"MaxExposurePerSymbolPercent": 50,
"MaxAccountDrawdownPercent": 10
```
Y/o agregar una validación al inicio que **no arranque el motor** si estos valores son 0 en modo Live/Testnet.

---

## 🟠 ERRORES CONCEPTUALES DE TRADING — Afectan rentabilidad

### TRADE-1: Los indicadores se alimentan con TICKS y con KLINES simultáneamente

**Archivos**: `DefaultTradingStrategy.ProcessTickAsync` (línea 89) + `ProcessKlineAsync` (línea 108)

**Problema**: Los indicadores reciben `Update(price)` tanto desde ticks (cada ~100ms) como desde klines (cada vela cerrada). En una estrategia de 1H:
- Los ticks alimentan el RSI con 36,000+ data points por hora (cada micro-fluctuación)
- Al cerrar la vela, el RSI recibe UN dato más

El RSI calculado sobre ticks NO es el RSI de 14 períodos de 1H. Es un RSI ultra-ruidoso que responde a cada fluctuación intradía. Las señales RSI oversold/overbought serán completamente diferentes a las de un RSI sobre velas.

**Impacto**: Señales basadas en RSI/MACD/EMA son INCORRECTAS para el timeframe configurado. Un RSI(14) sobre ticks de 1H tiene ~36,000 períodos en 14 horas, no 14 velas.

**Solución**: **Solo alimentar indicadores con klines cerradas** (ya ocurre en `ProcessKlineAsync`). Eliminar la alimentación de indicadores en `ProcessTickAsync`:
```csharp
// ProcessTickAsync: solo evalúa señales, NO actualiza indicadores
// Los indicadores ya se actualizan en ProcessKlineAsync
```
El `ProcessTickAsync` debe usarse SOLO para:
- Enviar ticks al frontend (SignalR)
- Evaluar SL/TP sobre posiciones abiertas (usa precio actual, no indicadores)

---

### TRADE-2: RuleEngine evalúa reglas de salida custom con snapshot de tick

**Archivo**: `RuleEngine.EvaluateExitRulesAsync` (líneas 218-256)

**Problema**: Las reglas de salida tipo "RSI > 65 → vender" se evalúan con el snapshot de indicadores del último tick. Pero si los indicadores se alimentan tanto con ticks como con klines (TRADE-1), el snapshot contiene valores de RSI sobre ticks, no sobre el timeframe real.

Incluso si se corrige TRADE-1, hay otro problema: las reglas de salida se evalúan en `ProcessSingleTickAsync` (cada tick). Pero después de corregir TRADE-1, los indicadores SOLO se actualizan con klines. Entonces el snapshot en los ticks tendrá valores de indicadores que NO se actualizaron desde la última vela cerrada.

**Impacto**: Las reglas de salida basadas en indicadores (no SL/TP) serían evaluadas con datos stale hasta que cierre la siguiente vela.

**Solución**: Las reglas de salida basadas en indicadores deben evaluarse SOLO al cierre de vela (en el kline loop), no en cada tick. Las reglas de salida basadas en precio (SL/TP/trailing) sí deben evaluarse en cada tick. Separar la evaluación:
- **Tick loop**: solo SL, TP, trailing stop (usan solo `currentPrice`)
- **Kline loop**: reglas custom (usan indicadores)

---

### TRADE-3: Bollinger Bands genera señales de reversión en mercados trending

**Archivo**: `DefaultTradingStrategy.DetermineSignalCandidate` (línea 464)

**Problema**:
```csharp
if (price <= bbGen.LowerBand!.Value)
    return (OrderSide.Buy, IndicatorType.BollingerBands);
```
"Precio toca banda inferior → Buy" es una estrategia de **mean reversion**. Pero en un mercado en tendencia bajista, el precio puede caminar por la banda inferior durante días. Cada toque genera señal Buy → se abre Long → se activa SL → pérdida.

**Impacto**: Pérdidas consecutivas en mercados trending (que es cuando la mayoría del capital se mueve).

**Solución**: Las Bollinger Bands como generador de señal SOLO deben operar en régimen `Ranging`. El código ya detecta `MarketRegime.Trending` y filtra con ADX, pero el filtro de `HighVolatility` aplica ANTES de evaluar BB. Si el régimen es Trending, BB no debería generar señales. Agregar check:
```csharp
// BB solo genera señales en mercado lateral/ranging
if (_lastRegime?.Regime == MarketRegime.Trending) → no generar señal BB
```

---

### TRADE-4: No existe Sharpe Ratio anualizado — métricas de backtest difíciles de comparar

**Archivo**: `BacktestMetrics.Calculate` (línea 40)

**Problema**: El Sharpe Ratio se calcula como `meanReturn / stdDev` sobre los retornos absolutos de cada trade. Esto NO es el Sharpe Ratio estándar de la industria, que:
1. Usa retornos **porcentuales** (no absolutos)
2. Anualiza: `Sharpe = meanReturn / stdDev × sqrt(252)` (para diario)
3. Descuenta risk-free rate: `(meanReturn - riskFreeRate) / stdDev`

Un Sharpe de 0.5 en trades de $0.01 es el mismo que en trades de $100 con el cálculo actual.

**Impacto**: Las métricas no son comparables con la industria. Un "Sharpe > 1.0" del checklist no tiene significado estándar.

**Solución**: Calcular sobre retornos porcentuales (`trade.NetPnL / investedAmount × 100`) y anualizar según la frecuencia de trading.

---

## 🟡 ISSUES DE DISEÑO — Mejoran robustez

> No bloquean producción pero reducen la calidad o agregan riesgo operativo.

### DESIGN-1: Double validation de exchange filters ✅ RESUELTO

El `BinanceSpotOrderExecutor.PlaceOrderAsync` ya confía en `OrderService` para los filtros (línea 78: "Los filtros de exchange ya fueron aplicados por OrderService"). No hay doble validación.

---

### DESIGN-2: Query a DB en cada tick para posiciones abiertas

**Archivo**: `StrategyEngine.ProcessSingleTickAsync` (línea 955)

**Problema**: `positionRepo.GetOpenByStrategyIdAsync` se ejecuta en CADA tick (~1/segundo × N estrategias). Para una cuenta con 5 estrategias, son ~432,000 queries/día solo para leer posiciones.

**Solución**: Cachear posiciones abiertas en memoria (`ConcurrentDictionary` en el runner) e invalidar cuando se abre/cierra una posición. Las posiciones cambian pocas veces vs la lectura constante.

---

### DESIGN-3: Spot pretende soportar Short en múltiples capas

**Archivos**: `PortfolioRiskManager` (línea 65), `Position.cs` (línea 39), `RuleEngine.cs`

**Problema**: `MaxPortfolioShortExposureUsdt`, PnL de posiciones Sell, trailing stop para shorts — todo dead code en Binance Spot.

**Acción**: No eliminar (sirve si se agrega Margin/Futures), pero documentar en código que solo Long está activo en Spot.

---

### DESIGN-4: PositionSizer fallback cuando ATR ≤ 0 retorna maxOrderAmount

**Archivo**: `PositionSizer.Calculate` (línea 36)

**Problema**: Si `atrValue <= 0`, devuelve `maxOrderAmountUsdt` como monto. Esto ignora completamente la gestión de riesgo basada en volatilidad y usa el máximo posible.

**Solución**: Cuando ATR no está disponible, usar un fallback conservador (ej: 50% del max) o bloquear la orden hasta que el ATR esté ready.

---

### DESIGN-5: Log de HTF EMA usa período hardcoded "20" en vez de la variable real

**Archivo**: `DefaultTradingStrategy.ProcessConfirmationKline` (línea 170)

**Problema**: `"HTF EMA({Period})={Ema:F2}"` usa `20` hardcoded en vez del período real configurado en `config.RiskConfig.ConfirmationEmaPeriod`.

**Solución**: Almacenar el período en un campo y usarlo en el log.

---

### DESIGN-6: Circuit breaker no tiene auto-reset

**Archivo**: `GlobalCircuitBreaker.cs`

**Problema**: Una vez abierto (`Trip`), el circuit breaker solo se cierra con `Reset()` manual (via endpoint del `SystemController`). Si el drawdown diario disparó el circuit breaker a las 3 AM, el bot queda detenido hasta que alguien manualmente lo resetee.

**Solución**: Implementar auto-reset al inicio del siguiente día UTC (nuevo día = nuevo drawdown diario). O un `HalfOpen` que permita una orden de prueba.

---

## 🟢 MEJORAS PARA PRODUCCIÓN — Post-MVP

> Implementar después de resolver CRIT y TRADE issues.

| ID | Mejora | Descripción | Prioridad |
|----|--------|-------------|-----------|
| IMP-1 | Reconciliación Binance ↔ DB | Worker periódico que verifica órdenes/posiciones locales contra Binance REST. Detecta desincronización. | Alta |
| IMP-2 | Alertas externas | Telegram/webhook para circuit breaker, drawdown, errores críticos. SignalR requiere que el frontend esté abierto. | Alta |
| IMP-3 | Caché de posiciones en memoria | `GetOpenByStrategyIdAsync` en cada tick → cachear en `StrategyRunnerState` | Media |
| IMP-4 | Motivo de cierre en Position | Enum `CloseReason { StopLoss, TakeProfit, TrailingStop, ExitRule, Manual }` para análisis | Media |
| IMP-5 | Walk-forward analysis | 70/30 split en optimizador. Actualmente todo el dataset es in-sample = overfitting. | Alta |
| IMP-6 | Redis fallback a memoria | Si Redis cae, usar `IMemoryCache`. Actualmente el bot no arranca sin Redis. | Baja |
| IMP-7 | Fee tracking real en Live | UserDataStream recibe `update.Fee` y `update.FeeAsset`. Si BNB está habilitado, la fee puede ser en BNB no en USDT. El `FeeAndSlippageCalculator` asume siempre quote asset. | Alta |
| IMP-8 | Partial fills en Live | `OrderSyncHandler.HandleOrderFilledAsync` asume fill completo. Un partial fill abre posición parcial pero no maneja la segunda parte. | Alta |
| IMP-9 | Backtest: equity tracking con capital real | Usar `initialBalance - investedInOpenPosition` como balance disponible, no un balance ficticio | Alta |

---

## 🏁 ETAPA 7 — Correcciones pre-Testnet

> **Requisito**: resolver CRIT-1 a CRIT-6 y TRADE-1 a TRADE-4 antes de validar en Testnet.

### Prioridad de resolución

1. **TRADE-1** (indicadores alimentados con ticks Y klines) — Más impactante, cambia toda la lógica de señales
2. **CRIT-1** (ATR solo con Close) — Necesario para que PositionSizer y ATR-SL funcionen correctamente
3. **TRADE-2** (reglas de salida en tick loop) — Dependiente de TRADE-1
4. **CRIT-2** (MIN_NOTIONAL Market orders) — Bloquea ejecución de órdenes pequeñas
5. **TRADE-3** (BB en trending) — Genera pérdidas innecesarias
6. **CRIT-6** (GlobalRisk deshabilitados) — Red de seguridad obligatoria
7. **CRIT-4** (Backtest balance ficticio) — Métricas incorrectas para decisiones
8. **TRADE-4** (Sharpe no anualizado) — Métricas de comparación
9. **CRIT-3** (UserDataStream sin CT) — Edge case en shutdown
10. **CRIT-5** (Race condition kline/tick) — Ya mitigado con semáforo, documentar

### Checklist pre-live

- [x] Bugs BUG-1 a BUG-7 corregidos (376/376 tests passing)
- [ ] **CRIT-1 a CRIT-6 resueltos con tests**
- [ ] **TRADE-1 a TRADE-4 resueltos con tests**
- [ ] Testnet operando estable ≥ 2 semanas sin errores críticos
- [ ] Sharpe Ratio anualizado walk-forward > 1.0 en al menos 2 estrategias
- [ ] Max drawdown en Testnet < 15%
- [ ] Kill switch global probado (manual + automático vía drawdown)
- [ ] Backup de BD verificado
- [ ] API Key de autenticación configurada (`TRADINGBOT_API_KEY`)
- [ ] `appsettings.json` sin secrets hardcoded
- [ ] GlobalRisk configurados con valores conservadores (no 0)
- [ ] IMP-1 (Reconciliación) implementada
- [ ] IMP-7 (Fee tracking BNB) implementada

### Activación por fases

1. **Alpha**: 1 estrategia, 1 símbolo, máx $50 USDT, 2 semanas
2. **Beta**: 2-3 estrategias, máx $200 USDT, 1 mes
3. **Producción**: escala gradual según performance real

---

## 🔧 Referencia técnica

### Comandos

```powershell
# Build + Tests
dotnet build
dotnet test

# Migraciones EF Core
dotnet ef migrations add <Nombre> --project src\TradingBot.Infrastructure --startup-project src\TradingBot.API
dotnet ef database update --project src\TradingBot.Infrastructure --startup-project src\TradingBot.API

# Docker
docker compose up -d        # Infraestructura (Postgres + Redis)
docker compose -f docker-compose.prod.yml up -d  # Producción
```

### Convenciones establecidas

- **Enums en JSON**: siempre como strings (`JsonStringEnumConverter` en MVC + minimal API)
- **Locale**: usar `CultureInfo.InvariantCulture` para parseo de decimales
- **Tests de integración**: `[Collection(nameof(SharedFactoryCollection))]`
- **EF Core owned entities**: `FixNewOwnedEntitiesTrackedAsModified()` en `SaveChangesAsync`
- **`decimal` en `[InlineData]`**: no permitido — usar `TheoryData<>` con `[MemberData]`
- **`InternalsVisibleTo`**: configurado en `TradingBot.Application.csproj` para tests + NSubstitute

### Flujo completo del motor

```
WebSocket Kline (vela cerrada)
  → ITradingStrategy.ProcessKlineAsync → actualiza indicadores → ¿señal?
  → IRuleEngine.EvaluateAsync → ¿orden de entrada?
  → IRiskManager.ValidateOrderAsync → ¿aprobada?
  → IOrderService.PlaceOrderAsync → Paper/Live/DryRun

WebSocket Ticker (cada tick)
  → ITradingNotifier.NotifyMarketTickAsync → SignalR → Dashboard
  → Para cada posición abierta:
    → position.UpdatePrice(tick.LastPrice)
    → IRuleEngine.EvaluateExitRulesAsync → ¿SL/TP/trailing/regla de salida?
    → IOrderService.PlaceOrderAsync → cierra posición
```

---

## 🤖 PROMPT PARA SIGUIENTE CHAT — Implementar correcciones

> Copiar este bloque completo al inicio de un nuevo chat para dar contexto.

```
Eres un experto en .NET y trading de Binance. Necesito que implementes las correcciones
críticas detectadas en la auditoría del proyecto TradingBot.

CONTEXTO: Bot de trading para Binance Spot en .NET 10, Clean Architecture.
Lee `.github/copilot-instructions.md` para convenciones y `.github/Pasos_A_Seguir.md`
para el estado actual y los issues detectados.

ISSUES A RESOLVER (en orden de prioridad):

═══════════════════════════════════════════════════════════════════
TRADE-1: Indicadores alimentados con ticks Y klines simultáneamente
═══════════════════════════════════════════════════════════════════
Archivo: DefaultTradingStrategy.ProcessTickAsync (línea 89)
Problema: `foreach (var indicator in _indicators.Values) indicator.Update(price)`
se ejecuta tanto en ProcessTickAsync como en ProcessKlineAsync.
Los indicadores (RSI, MACD, EMA) reciben miles de data points por hora de ticks,
más uno por kline. El RSI resultante no corresponde al timeframe configurado.

Solución: ELIMINAR la actualización de indicadores en ProcessTickAsync.
Solo mantenerla en ProcessKlineAsync (vela cerrada). ProcessTickAsync solo
debe evaluar reglas de salida SL/TP (que usan currentPrice, no indicadores).
La evaluación de EvaluateSignal también debe moverse a ProcessKlineAsync.

═══════════════════════════════════════════════════════════════════
CRIT-1: ATR usa solo Close — subestima volatilidad real
═══════════════════════════════════════════════════════════════════
Archivo: AtrIndicator.cs
Problema: True Range = |close - prevClose|. Debería ser:
max(High-Low, |High-prevClose|, |Low-prevClose|)

Solución: Agregar método UpdateOhlc(high, low, close) al AtrIndicator.
Llamar desde ProcessKlineAsync donde se tiene OHLC completo.
No cambiar ITechnicalIndicator.Update(decimal) — agregar interfaz IOhlcIndicator.

═══════════════════════════════════════════════════════════════════
TRADE-2: Reglas de salida custom evaluadas en cada tick
═══════════════════════════════════════════════════════════════════
Archivo: StrategyEngine.ProcessSingleTickAsync → llama EvaluateExitRulesAsync
que evalúa TODAS las reglas de salida incluidas las que dependen de indicadores.

Solución: En ProcessSingleTickAsync, solo evaluar SL/TP/trailing (precio puro).
Las reglas de salida basadas en indicadores (RuleType.Exit con condiciones de RSI,
MACD, etc.) solo evaluarlas al cierre de vela (ProcessSingleKlineAsync).

═══════════════════════════════════════════════════════════════════
CRIT-2: MIN_NOTIONAL no se valida para Market orders
═══════════════════════════════════════════════════════════════════
Archivo: ExchangeSymbolFilters.ValidateAndAdjust (línea 81)
Problema: Solo valida si adjustedPrice.HasValue (limitPrice).
Market orders no tienen limitPrice → MIN_NOTIONAL nunca se valida.

Solución: Agregar parámetro estimatedPrice a ValidateAndAdjust.
Si limitPrice es null, usar estimatedPrice para calcular notional.
Pasar order.EstimatedPrice desde OrderService.

═══════════════════════════════════════════════════════════════════
TRADE-3: Bollinger Bands genera señales en mercados trending
═══════════════════════════════════════════════════════════════════
Archivo: DefaultTradingStrategy.DetermineSignalCandidate (línea 461-468)
Solución: BB solo genera señales si _lastRegime?.Regime es Ranging o Unknown.
En Trending, BB no genera señal (solo puede confirmar como votante).

═══════════════════════════════════════════════════════════════════
CRIT-6: GlobalRisk deshabilitados por defecto
═══════════════════════════════════════════════════════════════════
Archivo: appsettings.json + StrategyEngine.ExecuteAsync
Solución: Cambiar defaults a valores conservadores y agregar validación
al inicio del motor que no arranque si MaxAccountDrawdownPercent=0
en modo Live/Testnet.

═══════════════════════════════════════════════════════════════════
CRIT-4: Backtest usa balance ficticio para PositionSizer
═══════════════════════════════════════════════════════════════════
Archivo: BacktestEngine.RunAsync (línea 174)
Solución: Agregar parámetro initialBalance al RunBacktestCommand.
Usar equity curve (initialBalance + realizedPnL) como balance dinámico.

═══════════════════════════════════════════════════════════════════
TRADE-4: Sharpe Ratio no anualizado
═══════════════════════════════════════════════════════════════════
Archivo: BacktestMetrics.Calculate
Solución: Usar retornos porcentuales y anualizar con sqrt(N).

═══════════════════════════════════════════════════════════════════
CRIT-3: UserDataStream sin CancellationToken en Task.Run
═══════════════════════════════════════════════════════════════════
Archivo: UserDataStreamService.OnOrderUpdate (línea 185)
Solución: Pasar _cts.Token y esperar drain en StopAsync.

INSTRUCCIONES:
1. Implementa cada corrección empezando por TRADE-1 (es la base para las demás)
2. Después de cada corrección, compila y corre los tests
3. Actualiza tests existentes que dependan del comportamiento cambiado
4. Agrega tests nuevos para las correcciones
5. Actualiza .github/Pasos_A_Seguir.md marcando como completado
```