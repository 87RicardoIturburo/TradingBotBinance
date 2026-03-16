# TradingBot — Plan de Correcciones para Producción

> **Propósito:** Este documento es un plan de ejecución completo para que un nuevo chat de
> Copilot (u otro desarrollador) pueda implementar TODAS las correcciones necesarias para
> llevar este bot de trading de estado MVP a calidad de producción profesional.
>
> **Regla principal:** Cada punto incluye: diagnóstico, archivos involucrados, código afectado
> y la solución esperada. Implementar en el orden de prioridades indicado.

---

## Estado actual del proyecto (2026-03-16)

| Métrica | Valor |
|---------|-------|
| Build | ✅ Compilación correcta (8 proyectos) |
| Tests | ✅ 358/358 passing (Core 74, Application 276, Integration 8) |
| TFM | .NET 10 / C# 14 |
| Stack | Clean Architecture, EF Core + PostgreSQL, Redis, Binance.Net, MediatR, SignalR, Blazor WASM |
| Documentación | `.github/PROJECT.md`, `.github/copilot-instructions.md`, `.github/Pasos_A_Seguir.md` |

---

## 🔴 PRIORIDAD 1 — Fallas Críticas (Bloquean uso con dinero real)

Estas fallas pueden causar **pérdida financiera real**. No operar ni en Testnet sin corregirlas.

---

### 1.1 — PnL no descuenta comisiones (fees) ni slippage

**Severidad:** 🔴 CRÍTICA — invalida TODA la gestión de riesgo y la esperanza matemática.

**Diagnóstico:**
El método `Position.Close()` calcula el PnL bruto sin descontar comisiones de Binance:

```csharp
// src/TradingBot.Core/Entities/Position.cs — líneas 85-93
RealizedPnL = Side == OrderSide.Buy
    ? (closePrice.Value - EntryPrice.Value) * Quantity.Value
    : (EntryPrice.Value - closePrice.Value) * Quantity.Value;
```

Si Binance cobra 0.1% por lado (0.2% round-trip), una posición con +0.15% bruto parece
ganadora pero en realidad perdió -0.05% neto. El `RiskManager.GetMathematicalExpectancyAsync()`
usa este PnL inflado, permitiendo que estrategias perdedoras sigan operando.

El campo `Order.Fee` ya existe (`decimal Fee`) pero nunca se propaga a `Position`.

**Archivos a modificar:**
- `src/TradingBot.Core/Entities/Position.cs` — agregar campo `EntryFee`, `ExitFee`, modificar `Close()` y `UnrealizedPnL`
- `src/TradingBot.Application/Services/OrderService.cs` — propagar fees reales a la posición al crear/cerrar
- `src/TradingBot.Application/Backtesting/BacktestEngine.cs` — usar fees en PnL de backtest
- Tests afectados: todos los de RiskManager que validen esperanza matemática, y los de Position

**Solución esperada:**
1. Agregar a `Position`: `public decimal EntryFee { get; private set; }` y `public decimal ExitFee { get; private set; }`
2. Modificar `Position.Open()` para aceptar `decimal entryFee = 0m`
3. Modificar `Position.Close(Price closePrice, decimal exitFee = 0m)` para calcular:
   `RealizedPnL = grossPnL - EntryFee - exitFee`
4. Modificar `UnrealizedPnL` para estimar fee de salida
5. En `OrderService.SimulatePaperTradeAsync` y `HandlePositionAsync`: pasar `order.Fee` a la posición
6. Crear migración EF Core: `dotnet ef migrations add AddPositionFeeColumns --project src\TradingBot.Infrastructure --startup-project src\TradingBot.API`
7. Verificar que `BacktestEngine` usa `FeeAndSlippageCalculator.CalculateRoundTripImpact()`
8. Actualizar todos los tests que verifican PnL

**Contexto extra:**
- `FeeAndSlippageCalculator` ya existe en `src/TradingBot.Application/RiskManagement/FeeAndSlippageCalculator.cs` — reutilizar
- `TradingFeeConfig` en `src/TradingBot.Application/RiskManagement/TradingFeeConfig.cs` con `MakerFeePercent=0.001m`, `TakerFeePercent=0.001m`

---

### 1.2 — Race condition en validación de saldo + ejecución de órdenes

**Severidad:** 🔴 CRÍTICA — puede causar órdenes rechazadas y posiciones huérfanas.

**Diagnóstico:**
Dos estrategias generan señales simultáneas. Ambas pasan validación de saldo en paralelo,
pero al ejecutar solo una tiene saldo real. La otra falla en Binance con -2010.

```
Estrategia A: RiskManager.ValidateOrder → saldo = 100 USDT → ✅
Estrategia B: RiskManager.ValidateOrder → saldo = 100 USDT → ✅ (paralelo)
Estrategia A: OrderService.PlaceOrder → gasta 80 USDT → ✅
Estrategia B: OrderService.PlaceOrder → saldo = 20 USDT → ❌ -2010
```

**Archivos a crear/modificar:**
- CREAR: `src/TradingBot.Core/Interfaces/Services/IOrderExecutionLock.cs`
- CREAR: `src/TradingBot.Application/Services/OrderExecutionLock.cs`
- Modificar: `src/TradingBot.Application/Services/OrderService.cs` — usar el lock
- Modificar: `src/TradingBot.Application/ApplicationServiceExtensions.cs` — registrar Singleton

**Solución esperada:**
1. `IOrderExecutionLock` con `AcquireAsync(string quoteAsset, CancellationToken, TimeSpan? timeout)`
2. Implementar con `ConcurrentDictionary<string, SemaphoreSlim>` (semáforo por quote asset)
3. En `OrderService.PlaceOrderAsync`: ValidateOrder + PlaceOrder dentro del lock
4. Default timeout: 10 segundos. Registrar como Singleton en DI.

---

### 1.3 — Sin circuit breaker global para detener todo el trading

**Severidad:** 🔴 CRÍTICA — si Binance devuelve datos erróneos, las estrategias siguen operando.

**Diagnóstico:**
No existe mecanismo central que detenga TODAS las estrategias ante problemas del exchange.
El `GlobalRiskSettings.MaxDailyLossUsdt` solo se evalúa al enviar órdenes, no proactivamente.

**Archivos a crear/modificar:**
- CREAR: `src/TradingBot.Core/Interfaces/Services/IGlobalCircuitBreaker.cs`
- CREAR: `src/TradingBot.Application/RiskManagement/GlobalCircuitBreaker.cs`
- Modificar: `src/TradingBot.Application/Strategies/StrategyEngine.cs` — verificar antes de cada tick
- Modificar: `src/TradingBot.Application/Services/OrderService.cs` — verificar antes de ejecutar
- Modificar: `src/TradingBot.API/Controllers/SystemController.cs` — endpoints estado/reset

**Solución esperada:**
1. `IGlobalCircuitBreaker`: `IsOpen`, `TripReason`, `TrippedAt`, `Trip(reason)`, `Reset()`, `RecordExchangeError(source)`, `RecordExchangeSuccess()`
2. Auto-trip si: 10+ errores REST en 5 min, 3+ rate limits en 1 min, drawdown > límite, WebSockets caídos
3. `StrategyEngine` verifica `IsOpen` al inicio de `ProcessSingleTickAsync`
4. Notificar vía `ITradingNotifier`. Endpoints: `GET /api/system/circuit-breaker`, `POST .../reset`

---

### 1.4 — Órdenes de cierre duplicadas al reiniciar

**Severidad:** 🔴 CRÍTICA — puede crear posiciones invertidas no deseadas.

**Diagnóstico:**
`StrategyEngine.EvaluateOpenPositionsOnStartupAsync()` no verifica si ya existe una orden
de cierre pendiente antes de crear otra. Crash loop = doble cierre = posición invertida.

```csharp
// StrategyEngine.cs — sin verificación de idempotencia
foreach (var position in openPositions)
{
    var exitResult = await ruleEngine.EvaluateExitRulesAsync(...);
    if (exitResult.IsSuccess && exitResult.Value is { } exitOrder)
        await orderService.PlaceOrderAsync(exitOrder, cancellationToken);
        // ← No verifica si ya hay orden de cierre pendiente
}
```

**Archivos a modificar:**
- `src/TradingBot.Core/Interfaces/Repositories/IOrderRepository.cs` — agregar `HasPendingCloseOrderAsync`
- `src/TradingBot.Infrastructure/Persistence/Repositories/OrderRepository.cs` — implementar
- `src/TradingBot.Application/Strategies/StrategyEngine.cs` — verificar en startup y tick loop

**Solución:**
1. `HasPendingCloseOrderAsync(Guid strategyId, Symbol symbol, OrderSide exitSide, CancellationToken)`
2. Query: `Orders.AnyAsync(o => strategyId && symbol && side && !IsTerminal)`
3. Verificar antes de cada `PlaceOrderAsync` de cierre

---

### 1.5 — Sin protección contra slippage en órdenes Market

**Severidad:** 🔴 CRÍTICA — en flash crash puede ejecutar a precio radicalmente diferente.

**Diagnóstico:**
Órdenes Market se envían sin verificar spread bid-ask. `MarketTickReceivedEvent` ya tiene
`BidPrice` y `AskPrice` pero nadie los usa para validar.

**Archivos a modificar:**
- `src/TradingBot.Core/ValueObjects/RiskConfig.cs` — agregar `MaxSpreadPercent` (default 1.0%)
- `src/TradingBot.Core/Interfaces/Services/IMarketDataService.cs` — agregar `GetLastBidAsk(Symbol)`
- `src/TradingBot.Infrastructure/Binance/MarketDataService.cs` — almacenar último bid/ask en `ConcurrentDictionary`
- `src/TradingBot.Application/Services/OrderService.cs` — validar spread antes de Market orders
- Migración EF Core (RiskConfig es owned entity de TradingStrategy)

**Solución:**
1. `MaxSpreadPercent` en `RiskConfig`. `GetLastBidAsk` desde cache en memoria.
2. Spread = `(ask - bid) / midPrice * 100`. Si > max → `DomainError.RiskLimitExceeded`
3. Warning cuando spread > 50% del máximo

---

### 1.6 — Channel ilimitado para ticks (riesgo de OutOfMemoryException)

**Severidad:** 🟠 ALTA — crash si procesamiento se atrasa.

**Archivo:** `src/TradingBot.Infrastructure/Binance/MarketDataService.cs`

**Solución:**
```csharp
Channel.CreateBounded<MarketTickReceivedEvent>(new BoundedChannelOptions(1000)
{
    SingleReader = false, SingleWriter = true,
    FullMode = BoundedChannelFullMode.DropOldest
});
```
Agregar contador de drops y métrica `trading.ticks_dropped`.

---

## 🟡 PRIORIDAD 2 — Mejoras de Calidad de Trading

---

### 2.1 — Indicadores sobre ticks crudos en lugar de velas de timeframe

**Severidad:** 🟡 ALTA — señales incomparables con cualquier plataforma profesional.

**Diagnóstico:**
El bot suscribe a `SubscribeToTickerUpdatesAsync` (ticker 24h) y alimenta cada tick a los
indicadores. RSI(14) se calcula sobre 14 ticks, NO 14 velas cerradas. Los resultados son
**incomparables** con TradingView y dan señales ruidosas. Dos ejecuciones con diferente
timing de ticks dan señales diferentes.

**Concepto clave:** Un bot profesional usa **velas cerradas** para indicadores. El tick crudo
se usa solo para SL/TP en tiempo real y dashboard.

**Archivos a crear/modificar:**
- CREAR: `src/TradingBot.Core/Enums/CandleInterval.cs` — enum propio
- CREAR: `src/TradingBot.Core/Events/KlineClosedEvent.cs`
- Modificar: `src/TradingBot.Core/Entities/TradingStrategy.cs` — agregar `CandleInterval Timeframe`
- Modificar: `src/TradingBot.Core/Interfaces/Services/IMarketDataService.cs` — `SubscribeKlinesAsync`, `GetKlineStreamAsync`
- Modificar: `src/TradingBot.Infrastructure/Binance/MarketDataService.cs` — `SubscribeToKlineUpdatesAsync`, emitir solo con `Final == true`
- Modificar: `src/TradingBot.Core/Interfaces/Trading/ITradingStrategy.cs` — agregar `ProcessKlineAsync`
- Modificar: `src/TradingBot.Application/Strategies/DefaultTradingStrategy.cs` — indicadores solo en velas cerradas
- Modificar: `src/TradingBot.Application/Strategies/StrategyEngine.cs` — dos streams: kline + ticker
- Migración EF Core, DTOs frontend

**Solución clave:**
1. Dos streams: **Kline** (indicadores + señales) + **Ticker** (SL/TP + dashboard)
2. Indicadores se actualizan SOLO con `KlineClosedEvent` (vela cerrada)
3. `CandleInterval`: `OneMinute, FiveMinutes, FifteenMinutes, ThirtyMinutes, OneHour, FourHours, OneDay`
4. Binance.Net: `SubscribeToKlineUpdatesAsync(symbol, interval, onMessage)`, `update.Data.Data.Final` = vela cerrada
5. Doc: https://developers.binance.com/docs/binance-spot-api-docs/web-socket-streams#klinecandlestick-streams

---

### 2.2 — Domain Events declarados pero nunca despachados

**Severidad:** 🟡 MEDIA

Los 10 domain events en `src/TradingBot.Core/Events/` se generan pero nunca se despachan.
`TradingBotDbContext.SaveChangesAsync()` los limpia sin publicarlos.

**Solución:**
1. `DomainEvent` implementa `MediatR.INotification`
2. En `SaveChangesAsync`: recoger events → persist → `_mediator.Publish(event)`
3. Handlers: `OrderPlacedEventHandler`, `StrategyActivatedEventHandler`, `RiskLimitExceededEventHandler`

---

### 2.3 — `TradingMetrics` (OpenTelemetry) nunca invocado

**Severidad:** 🟡 MEDIA

Registrado Singleton en DI pero no inyectado en ningún servicio. Contadores muertos.

**Solución:**
1. Inyectar en `StrategyEngine` (singleton) y `OrderService`
2. Llamar `RecordTickProcessed`, `RecordSignalGenerated`, `RecordOrderPlaced/Failed`
3. Medir latencia tick→orden con `Stopwatch`

---

### 2.4 — `PortfolioRiskManager` fuera de DI

**Severidad:** 🟡 MEDIA

`_portfolioRisk = new PortfolioRiskManager(positionRepository)` en `RiskManager`.
Registrar Scoped en DI, inyectar por constructor, eliminar `new`.

---

### 2.5 — BacktestEngine duplica lógica de SL/TP

**Severidad:** 🟡 MEDIA

Lógica inline en vez de `ruleEngine.EvaluateExitRulesAsync()` (que ya recibe como parámetro).
Refactorizar para consistencia con ejecución real.

---

## 🔵 PRIORIDAD 3 — Seguridad y Robustez

---

### 3.1 — API Key expuesta en frontend Blazor WASM

**Severidad:** 🟠 ALTA (seguridad)

`builder.Configuration["ApiKey"]` en `src/TradingBot.Frontend/Program.cs` — descargable.
Implementar BFF Pattern: `POST /api/auth/login` → cookie HttpOnly. Eliminar key de config WASM.

### 3.2 — UserDataStreamService no integra con posiciones

Recibe `executionReport` de Binance pero no crea/cierra posiciones al llenar órdenes.
Crear `IOrderSyncHandler` compartido entre `OrderService` y `UserDataStreamService`.

### 3.3 — Persistir estado de indicadores en Redis

Warm-up con `maxPeriod + 10` velas insuficiente para MACD/ADX. Agregar `SerializeState()`/`DeserializeState()`
a `ITechnicalIndicator`. Redis key: `indicator:{strategyId}:{type}`, TTL 24h.

---

## 🟢 PRIORIDAD 4 — Mejoras Profesionales

### 4.1 — Multi-Timeframe Analysis (PrimaryTimeframe + ConfirmationTimeframe)
### 4.2 — Limit orders con timeout (reduce fees maker vs taker, elimina slippage)
### 4.3 — Correlation IDs en logging (`ILogger.BeginScope` + Serilog `FromLogContext`)
### 4.4 — Dashboard de métricas en frontend (SignalR + TradingMetrics cada 5s)
### 4.5 — Modo Dry-Run (conecta WebSocket real, loguea pero no ejecuta — agregar a `TradingMode` enum)

---

## 📋 Checklist de Ejecución

### 🔴 Prioridad 1
- [X] 1.1 — PnL con fees
- [X] 1.2 — Semáforo global de ejecución
- [X] 1.3 — Circuit breaker global
- [X] 1.4 — Idempotencia en órdenes de cierre
- [X] 1.5 — Spread guard
- [X] 1.6 — Bounded channel

### 🟡 Prioridad 2
- [X] 2.1 — Indicadores sobre velas
- [X] 2.2 — Domain events dispatch
- [X] 2.3 — TradingMetrics integración
- [X] 2.4 — PortfolioRiskManager en DI
- [X] 2.5 — BacktestEngine usa RuleEngine

### 🔵 Prioridad 3
- [ ] 3.1 — API Key del frontend
- [ ] 3.2 — UserDataStream + posiciones
- [ ] 3.3 — Estado indicadores en Redis

### 🟢 Prioridad 4
- [ ] 4.1 — Multi-Timeframe
- [ ] 4.2 — Limit con timeout
- [ ] 4.3 — Correlation IDs
- [ ] 4.4 — Dashboard métricas
- [ ] 4.5 — Modo Dry-Run

---

## 📐 Reglas para el chat que implemente estos cambios

1. **Seguir el orden de prioridades** — no saltar a P3 sin completar P1
2. **Compilar y correr tests** después de CADA punto (`dotnet build`, `dotnet test`)
3. **Crear migraciones EF Core** al modificar entidades persistidas:
   `dotnet ef migrations add <Nombre> --project src\TradingBot.Infrastructure --startup-project src\TradingBot.API`
4. **No romper tests existentes** — actualizar los que fallen por cambios intencionados
5. **Crear tests nuevos** para cada funcionalidad (NSubstitute + FluentAssertions + xUnit)
6. **Convenciones del proyecto** — ver `.github/copilot-instructions.md`:
   - NSubstitute para mocking (nunca Moq)
   - FluentAssertions para aserciones
   - xUnit como framework
   - Nombrar tests: `[Método]_[Escenario]_[Resultado]`
   - Async con `CancellationToken`, `sealed` por defecto, `record` para DTOs
7. **Actualizar docs** — `.github/Pasos_A_Seguir.md` y `.github/PROJECT.md`
8. **No modificar código auto-generado** (migraciones existentes, `*.g.cs`)
9. **Cada corrección completa** — no dejar TODOs ni código a medias
10. **IOptions&lt;T&gt;** para configuración, nunca `IConfiguration` directamente en servicios