# TradingBot — Pasos a Seguir

> **Contexto para Copilot en un nuevo chat.**
> Este archivo describe el estado actual del proyecto y los pasos concretos para continuar.
> Leer junto con `.github/PROJECT.md` y `.github/copilot-instructions.md`.

---

## 📊 Estado actual verificado (2026-03-09)

| Métrica | Valor |
|---------|-------|
| Build | ✅ Compilación correcta (8 proyectos) |
| Tests | ✅ **234/234 passing** — Core (47) + Application (179) + Integration (8) |
| TFM | .NET 10 / C# 14 |
| Migraciones EF Core | 4 aplicadas: `InitialCreate`, `UseXminConcurrencyToken`, `FixVersionColumnType`, `AddOptimizationRanges` |
| Multi-project launch | `TradingBot.slnLaunch.user` con API + Frontend |
| **E2E** | ✅ Probado: CRUD estrategias, indicadores, reglas, activar/desactivar, eliminar, SignalR ticks |

### Capas completadas

| Capa | Estado | Archivos clave |
|------|--------|----------------|
| **Core** | ✅ 10 enums, 10 VOs, 4 entidades, 10 eventos, 12 interfaces | `src/TradingBot.Core/` |
| **Infrastructure** | ✅ EF Core + Npgsql, 3 repos, Binance.Net WS+REST, Redis cache, Serilog | `src/TradingBot.Infrastructure/` |
| **Application** | ✅ 14 MediatR handlers, StrategyEngine (BackgroundService), RuleEngine, RiskManager (esperanza matemática), OrderService, 7 indicadores (RSI/EMA/SMA/MACD/Bollinger/Fibonacci/LinReg), multi-indicator confirmation | `src/TradingBot.Application/` |
| **API** | ✅ 5 controllers (27 endpoints), SignalR Hub, ErrorHandling middleware, SignalRTradingNotifier, 4 strategy templates | `src/TradingBot.API/` |
| **Frontend** | ✅ 7 páginas Blazor WASM (Dashboard, Strategies, StrategyDetail, Orders, Positions, Backtest, Optimizer), TradingApiClient, SignalR | `src/TradingBot.Frontend/` |
| **Tests** | ✅ Core.Tests (47), Application.Tests (179), Integration.Tests (8) | `tests/` |

### Flujo completo implementado (no probado end-to-end)

```
Frontend (Blazor WASM)
  ├─ POST /api/strategies                    → Crear estrategia
  ├─ GET  /api/strategies/templates           → Plantillas predefinidas (4)
  ├─ POST /api/strategies/{id}/indicators    → Agregar RSI/EMA/SMA/MACD/Bollinger
  ├─ PUT  /api/strategies/{id}/indicators    → Editar parámetros de indicador
  ├─ POST /api/strategies/{id}/rules         → Agregar regla con condiciones
  ├─ PUT  /api/strategies/{id}/rules/{ruleId} → Editar regla existente
  ├─ POST /api/strategies/{id}/activate      → Activar → StrategyEngine.ReloadStrategyAsync
  │
  │   StrategyEngine (BackgroundService)
  │   ├─ Carga estrategia de BD
  │   ├─ Warm-up indicadores con datos históricos de Binance
  │   ├─ Suscribe WebSocket → IAsyncEnumerable<MarketTick>
  │   └─ Loop por cada tick:
  │       ├─ ITradingNotifier.NotifyMarketTickAsync → SignalR → Dashboard
  │       ├─ ITradingStrategy.ProcessTickAsync → ¿señal?
  │       │   └─ ITradingNotifier.NotifySignalGeneratedAsync → SignalR
  │       ├─ IRuleEngine.EvaluateAsync → ¿orden?
  │       │   └─ IRiskManager.ValidateOrder → IOrderService.PlaceOrderAsync
  │       └─ IRuleEngine.EvaluateExitRulesAsync → stop-loss/take-profit
  │
  └─ SignalR (/hubs/trading)
      ├─ OnMarketTick      → Dashboard en tiempo real
      ├─ OnSignalGenerated → Señales
      ├─ OnOrderExecuted   → Órdenes
      └─ OnAlert           → Alertas
```

### Fixes importantes aplicados en sesiones anteriores

1. **`TradingBotDbContext.FixNewOwnedEntitiesTrackedAsModified()`** — Bug EF Core donde owned entities nuevas (TradingRules) se marcan como `Modified` → `DbUpdateConcurrencyException`
2. **`IResult` JSON serialization** — `JsonStringEnumConverter` configurado en `Microsoft.AspNetCore.Http.Json.JsonOptions` (no solo MVC)
3. **ErrorHandlingMiddleware** — `ex.ToString()` en non-Production (antes solo en Development)
4. **Integration tests** — `SharedFactoryCollection` + remover TODAS las registraciones EF/Npgsql + `IStrategyEngine` como singleton mock
5. **`SystemStatusDto` serialización** — `StrategyEngineStatus.Symbol` era un value object → JSON `{"value":"BTCUSDT"}` → frontend no podía deserializar → creado `StrategyEngineStatusDto` con `string Symbol` + `FromDomain()`
6. **`IsRunning` siempre false** — `_runners.Values.Any(r => r.IsProcessing)` fallaba porque en el instante del HTTP check ningún runner tenía un tick activo → cambiado a `!_runners.IsEmpty`

---

## 🎯 Casos de uso y operativa MVP

### Caso de uso 1: Paper Trading con RSI en BTCUSDT

1. Usuario abre Dashboard → ve estado del motor y conexión Binance
2. Va a Estrategias → crea "RSI BTC Paper" con symbol=BTCUSDT, mode=PaperTrading, maxOrder=100 USDT
3. Entra al detalle → agrega indicador RSI (period=14, overbought=70, oversold=30)
4. Agrega regla Entry: RSI < 30 → BuyMarket 50 USDT
5. Agrega regla Exit: RSI > 70 → SellMarket 50 USDT (o stop-loss/take-profit vía RiskConfig)
6. Activa la estrategia → StrategyEngine arranca el runner
7. Dashboard muestra ticks en tiempo real vía SignalR
8. Cuando RSI cruza 30 → se genera señal → se simula orden paper → se notifica al frontend

### Caso de uso 2: Hot-reload de estrategia activa

1. Estrategia activa procesando ticks
2. Usuario modifica parámetros del RSI (period=21) desde StrategyDetail
3. `UpdateStrategyCommand` → `StrategyConfigService.UpdateAsync` → `StrategyEngine.ReloadStrategyAsync`
4. El runner recarga la configuración sin detener el WebSocket
5. Frontend recibe `OnStrategyUpdated` vía SignalR

### Caso de uso 3: Gestión de riesgo

1. Orden generada por señal → pasa por `RiskManager.ValidateOrder`
2. RiskManager verifica: monto < maxOrderAmount, pérdida diaria < maxDailyLoss, posiciones abiertas < maxOpenPositions
3. Si pasa → `OrderService.PlaceOrderAsync` (Paper: simula con precio real de Binance REST)
4. Si falla → se genera `RiskLimitExceededEvent` + alerta al frontend

### Caso de uso 4: Monitoreo desde Dashboard

1. Dashboard se conecta a SignalR `/hubs/trading`
2. Muestra: estado del motor, conexión Binance, estrategias activas con métricas
3. Tabla de ticks en tiempo real (symbol, bid, ask, last, volume)
4. Alertas del sistema en tiempo real

---

## 📋 Paso 10 — Primera prueba end-to-end ✅

- [x] CRUD de estrategias funcional desde frontend
- [x] Agregar/eliminar indicadores y reglas desde StrategyDetail
- [x] Activar/desactivar estrategias
- [x] SignalR conectado, ticks en tiempo real
- [x] Fix Dashboard: `SystemStatusDto` con `StrategyEngineStatusDto` (Symbol como string)
- [x] Fix `IsRunning`: `!_runners.IsEmpty` en vez de `Any(r => r.IsProcessing)`

### Pre-requisitos
- Docker Desktop instalado y corriendo
- Variables de entorno opcionales para Binance Testnet (funciona sin keys en Paper Trading)

### Secuencia

```powershell
# 1. Levantar infraestructura
docker compose up -d

# 2. Verificar que Postgres y Redis están healthy
docker compose ps

# 3. Aplicar migraciones EF Core
cd src/TradingBot.API
dotnet ef database update --project ../TradingBot.Infrastructure

# 4. Arrancar (VS: F5 con perfil multi-project, o manual):
#    Terminal 1: dotnet run --project src/TradingBot.API
#    Terminal 2: dotnet run --project src/TradingBot.Frontend
```

### Verificar

1. **API arranca**: `https://localhost:7114/api/system/status` → `{"isRunning":false,"isConnected":false}`
2. **Frontend arranca**: `https://localhost:7017` → Dashboard visible
3. **SignalR conecta**: Dashboard muestra "Conectado" (o reconectando si no hay hub aún)
4. **CRUD funciona**: Crear estrategia desde `/strategies`, verificar en detalle

### Problemas probables y soluciones

| Problema | Causa | Solución |
|----------|-------|----------|
| `ConnectionRefused` en Postgres | Docker no levantó | `docker compose up -d` y esperar healthy |
| `ConnectionRefused` en Redis | Idem | Idem |
| CORS error en frontend | URLs no matchean | Verificar `FrontendUrl` en `appsettings.json` vs `launchSettings.json` |
| `dotnet ef` no encontrado | Tool no instalada | `dotnet tool install --global dotnet-ef` |
| Binance WS falla | Sin API keys | Funciona igual para Paper Trading (no necesita WS) |
| `DbUpdateConcurrencyException` al crear regla | Bug owned entities | Ya corregido con `FixNewOwnedEntitiesTrackedAsModified()` |

---

## 📋 Paso 11 — Tests unitarios Application layer ✅

**70 tests nuevos** agregados al proyecto `TradingBot.Application.Tests` (de 5 → 75 tests).

| Componente | Tests | Archivo |
|------------|-------|---------|
| `RuleEngine` | 33 (AND/OR/NOT, 12 comparadores via Theory, stop-loss, take-profit, exit rules, snapshot parsing) | `tests/.../Rules/RuleEngineTests.cs` |
| `DefaultTradingStrategy` | 11 (inicialización, RSI oversold→Buy, overbought→Sell, zona neutra, snapshot, cruce único, reset, reload) | `tests/.../Strategies/DefaultTradingStrategyTests.cs` |
| `OrderService` | 13 (paper trade fill, limit price, market price fail, buy→position, sell→close, live submit, cancel flows, sync, open orders) | `tests/.../Services/OrderServiceTests.cs` |
| `AddIndicatorCommandHandler` | 3 (not found, valid add, invalid params) | `tests/.../Commands/IndicatorAndRuleCommandHandlerTests.cs` |
| `RemoveIndicatorCommandHandler` | 3 (not found, remove existing, no-op when absent) | `tests/.../Commands/IndicatorAndRuleCommandHandlerTests.cs` |
| `AddRuleCommandHandler` | 4 (not found, valid add, empty name, zero amount) | `tests/.../Commands/IndicatorAndRuleCommandHandlerTests.cs` |
| `RemoveRuleCommandHandler` | 3 (not found, remove existing, rule not found) | `tests/.../Commands/IndicatorAndRuleCommandHandlerTests.cs` |

**Archivos creados:**
- `tests/TradingBot.Application.Tests/Rules/RuleEngineTests.cs`
- `tests/TradingBot.Application.Tests/Strategies/DefaultTradingStrategyTests.cs`
- `tests/TradingBot.Application.Tests/Services/OrderServiceTests.cs`
- `tests/TradingBot.Application.Tests/Commands/IndicatorAndRuleCommandHandlerTests.cs`

**Nota**: `StrategyConfigService` queda pendiente (requiere mocks de `ICacheService` + `IStrategyEngine` + `IStrategyRepository` con interacciones complejas de hot-reload).

---

## 📋 Paso 12 — Indicadores MACD + Bollinger Bands ✅

- [x] **`MacdIndicator`**: MACD Line = EMA(fast) − EMA(slow), Signal Line = EMA del MACD, Histogram = MACD − Signal. Compone internamente dos `EmaIndicator`.
- [x] **`BollingerBandsIndicator`**: Middle Band = SMA(period), Upper/Lower = Middle ± stdDev × σ, `BandWidth` = (Upper − Lower) / Middle.
- [x] **`IndicatorFactory`**: registrados MACD y BollingerBands en el switch.
- [x] **Tests**: `MacdIndicatorTests` (16), `BollingerBandsIndicatorTests` (21), `IndicatorFactoryTests` (6) — 43 tests nuevos.
- [x] **Fix locale**: `BollingerBandsIndicator.Name` usa `CultureInfo.InvariantCulture` para formato decimal consistente.

**Archivos creados:**
- `src/TradingBot.Application/Strategies/Indicators/MacdIndicator.cs`
- `src/TradingBot.Application/Strategies/Indicators/BollingerBandsIndicator.cs`
- `tests/TradingBot.Application.Tests/Indicators/MacdIndicatorTests.cs`
- `tests/TradingBot.Application.Tests/Indicators/BollingerBandsIndicatorTests.cs`
- `tests/TradingBot.Application.Tests/Indicators/IndicatorFactoryTests.cs`

---

## 📋 Paso 8B — Edición de indicadores/reglas + Plantillas predefinidas ✅

- [x] **Dominio**: `TradingStrategy.UpdateIndicator(IndicatorConfig)` — reemplaza indicador existente por tipo
- [x] **MediatR**: `UpdateIndicatorCommand`, `UpdateRuleCommand` (2 handlers nuevos, total 13)
- [x] **API**: `PUT indicators`, `PUT rules/{ruleId}`, `GET templates` (3 endpoints nuevos, total 19)
- [x] **`RuleDto` expandido**: incluye `Operator`, `Conditions[]`, `ActionType`, `AmountUsdt` + `ConditionDto` nuevo
- [x] **4 plantillas predefinidas** (`StrategyTemplates.cs`): RSI Oversold/Overbought, MACD Crossover, Bollinger+RSI Bounce, EMA Crossover (9/21)
- [x] **Frontend StrategyDetail**: edición in-place de indicadores y reglas (✏ → pre-populate form → PUT)
- [x] **Frontend Strategies**: sección template cards → crea estrategia + indicadores + reglas automáticamente
- [x] **Tabla de reglas mejorada**: muestra condiciones, acción, monto, operador con símbolos

**Archivos creados:**
- `src/TradingBot.API/Dtos/StrategyTemplates.cs`

**Archivos modificados:**
- `src/TradingBot.Core/Entities/TradingStrategy.cs` — `UpdateIndicator()`
- `src/TradingBot.Application/Commands/Strategies/IndicatorAndRuleCommands.cs` — 2 commands nuevos
- `src/TradingBot.API/Controllers/StrategiesController.cs` — 3 endpoints + `UpdateRuleRequest`
- `src/TradingBot.API/Dtos/Dtos.cs` — `RuleDto` expandido, `ConditionDto`
- `src/TradingBot.Frontend/Services/TradingApiClient.cs` — `UpdateIndicatorAsync`, `UpdateRuleAsync`, `GetTemplatesAsync`
- `src/TradingBot.Frontend/Models/Dtos.cs` — `UpdateRuleRequest`, `RuleConditionDto`, 5 template DTOs
- `src/TradingBot.Frontend/Pages/StrategyDetail.razor` — edit UI + comparator symbols
- `src/TradingBot.Frontend/Pages/Strategies.razor` — template cards + `ApplyTemplateAsync`

---

## 📋 Paso 13 — Página P&L y Posiciones ✅

- [x] MediatR queries: `GetOpenPositionsQuery`, `GetClosedPositionsQuery`, `GetPnLSummaryQuery`
- [x] `IPositionRepository.GetTotalRealizedPnLAsync` — query eficiente sin `DateTimeOffset.MinValue`
- [x] API controller: `PositionsController` (3 endpoints: open, closed, summary)
- [x] DTOs: `PositionDto`, `PnLSummaryDto`
- [x] Frontend: `Positions.razor` — resumen P&L por estrategia, posiciones abiertas con P&L no realizado, historial cerradas con win/loss rate
- [x] Fix `DateTimeOffset` PostgreSQL: `new DateTimeOffset(date, TimeSpan.Zero)` en vez de `DateTimeOffset.UtcNow.Date`

**Archivos creados:**
- `src/TradingBot.Application/Queries/Positions/PositionQueries.cs`
- `src/TradingBot.API/Controllers/PositionsController.cs`
- `src/TradingBot.Frontend/Pages/Positions.razor`

---

## 📋 Paso 14 — Backtesting Engine ✅

- [x] `IMarketDataService.GetKlinesAsync(symbol, from, to)` — descarga klines paginadas (1000/request) de Binance REST
- [x] `BacktestEngine` — recorre velas en memoria, simula estrategia completa (señales → reglas → órdenes → stop-loss/take-profit → P&L)
- [x] `BacktestResult` — métricas: P&L total, trades, win rate, max drawdown, equity curve, best/worst trade
- [x] MediatR `RunBacktestCommand` — carga estrategia de BD, descarga klines, warm-up indicadores, ejecuta backtest
- [x] API: `POST /api/backtest` → `BacktestController`
- [x] Frontend: `Backtest.razor` — seleccionar estrategia + rango de fechas, ejecutar, ver métricas + equity curve + tabla de trades
- [x] Tests: `BacktestEngineTests` (6 tests: no signals, signal+rule, stop-loss, take-profit, equity curve, cancellation)
- [x] Datos en memoria (opción A): descarga de Binance REST, no persiste en BD

**Archivos creados:**
- `src/TradingBot.Application/Backtesting/BacktestEngine.cs`
- `src/TradingBot.Application/Backtesting/RunBacktestCommand.cs`
- `src/TradingBot.API/Controllers/BacktestController.cs`
- `src/TradingBot.Frontend/Pages/Backtest.razor`
- `tests/TradingBot.Application.Tests/Backtesting/BacktestEngineTests.cs`

**Archivos modificados:**
- `src/TradingBot.Core/Interfaces/Services/IMarketDataService.cs` — `GetKlinesAsync` + `Kline` record
- `src/TradingBot.Infrastructure/Binance/MarketDataService.cs` — implementación paginada de `GetKlinesAsync`
- `src/TradingBot.API/Dtos/Dtos.cs` — `BacktestResultDto`, `BacktestTradeDto`, `EquityPointDto`, `RunBacktestRequest`
- `src/TradingBot.Frontend/Models/Dtos.cs` — DTOs frontend para backtest
- `src/TradingBot.Frontend/Services/TradingApiClient.cs` — `RunBacktestAsync`
- `src/TradingBot.Frontend/Layout/NavMenu.razor` — enlace a Backtest

---

## 📋 Paso A — Selector de Symbols con búsqueda ✅

- [x] `IMarketDataService.GetTradingSymbolsAsync(quoteAsset)` — llama `GetExchangeInfoAsync`, filtra por status Trading + quoteAsset
- [x] `MarketDataService` — implementación con Polly retry
- [x] API: `GET /api/system/symbols?quoteAsset=USDT` — con caché Redis 1 hora
- [x] `SymbolSelector.razor` — componente reutilizable con input + dropdown filtrado en tiempo real
- [x] `Strategies.razor` — InputText manual reemplazado por `<SymbolSelector>`

**Archivos creados:**
- `src/TradingBot.Frontend/Components/SymbolSelector.razor`

**Archivos modificados:**
- `src/TradingBot.Core/Interfaces/Services/IMarketDataService.cs` — `GetTradingSymbolsAsync` + `TradingSymbolInfo`
- `src/TradingBot.Infrastructure/Binance/MarketDataService.cs` — implementación
- `src/TradingBot.API/Controllers/SystemController.cs` — endpoint `/symbols` con cache
- `src/TradingBot.API/Dtos/Dtos.cs` — `SymbolInfoDto`
- `src/TradingBot.Frontend/Models/Dtos.cs` — `SymbolInfoDto`
- `src/TradingBot.Frontend/Services/TradingApiClient.cs` — `GetSymbolsAsync`
- `src/TradingBot.Frontend/Pages/Strategies.razor` — usa `SymbolSelector`
- `src/TradingBot.Frontend/_Imports.razor` — `@using Components`

---

## 📋 Paso B — Editar estrategia + Duplicar ✅

- [x] **Dominio**: `UpdateSymbol(Symbol)` y `UpdateMode(TradingMode)` — solo permitido si la estrategia está inactiva
- [x] **MediatR**: `UpdateStrategyCommand` expandido con `Symbol?` y `Mode?`
- [x] **MediatR**: `DuplicateStrategyCommand` — copia completa con indicadores y reglas
- [x] **API**: `PUT /api/strategies/{id}` ahora acepta symbol y mode
- [x] **API**: `POST /api/strategies/{id}/duplicate` — endpoint nuevo
- [x] **Frontend StrategyDetail**: botón ✏️ Editar (nombre, symbol, mode, risk config) + botón 📋 Duplicar
- [x] **Tests**: 4 nuevos tests dominio (UpdateSymbol/UpdateMode success + active failure)

**Archivos creados:**
- `src/TradingBot.Application/Commands/Strategies/DuplicateStrategyCommand.cs`

**Archivos modificados:**
- `src/TradingBot.Core/Entities/TradingStrategy.cs` — `UpdateSymbol()`, `UpdateMode()`
- `src/TradingBot.Application/Commands/Strategies/UpdateStrategyCommand.cs` — Symbol + Mode
- `src/TradingBot.API/Controllers/StrategiesController.cs` — `Duplicate` endpoint, `UpdateStrategyRequest` expandido
- `src/TradingBot.Frontend/Pages/StrategyDetail.razor` — formulario de edición completo + duplicar
- `src/TradingBot.Frontend/Models/Dtos.cs` — `UpdateStrategyRequest`
- `src/TradingBot.Frontend/Services/TradingApiClient.cs` — `UpdateStrategyAsync`, `DuplicateStrategyAsync`
- `tests/TradingBot.Core.Tests/Entities/TradingStrategyTests.cs` — 4 tests nuevos

---

## 📋 Paso C — Optimizador de parámetros ✅

- [x] `OptimizationEngine` — genera combinaciones cartesianas de parámetros, ejecuta BacktestEngine para cada una reutilizando klines
- [x] `ParameterRange` — define rango (min, max, step) para un parámetro; soporta indicadores (`RSI.period`) y risk config (`stopLossPercent`)
- [x] `OptimizationResult` — lista de resultados ordenados por P&L, con parámetros usados en cada combinación
- [x] `ApplyParameters` — clona estrategia en memoria y aplica parámetros de indicadores y risk config
- [x] MediatR `RunOptimizationCommand` — valida, descarga klines UNA vez, ejecuta optimizer
- [x] Límite de 500 combinaciones para evitar timeouts
- [x] API: `POST /api/backtest/optimize`
- [x] Frontend: `Optimizer.razor` — seleccionar estrategia + rangos dinámicos con sugerencias, tabla de resultados con Top 3 destacado
- [x] Tests: `OptimizationEngineTests` (7 tests: combinaciones, producto cartesiano, vacío, no signals, ranking, cancelación, propagación de parámetros)

**Archivos creados:**
- `src/TradingBot.Application/Backtesting/OptimizationEngine.cs`
- `src/TradingBot.Application/Backtesting/RunOptimizationCommand.cs`
- `src/TradingBot.Frontend/Pages/Optimizer.razor`
- `tests/TradingBot.Application.Tests/Backtesting/OptimizationEngineTests.cs`

**Archivos modificados:**
- `src/TradingBot.API/Controllers/BacktestController.cs` — endpoint `/optimize`
- `src/TradingBot.API/Dtos/Dtos.cs` — `OptimizationResultDto`, `OptimizationRunSummaryDto`, `ParameterRangeDto`, `RunOptimizationRequest`
- `src/TradingBot.Frontend/Models/Dtos.cs` — DTOs frontend para optimization
- `src/TradingBot.Frontend/Services/TradingApiClient.cs` — `RunOptimizationAsync`
- `src/TradingBot.Frontend/Layout/NavMenu.razor` — enlace a Optimizador

---

## 📋 Paso D — Matemáticas avanzadas para señales inteligentes ✅

- [x] **FibonacciIndicator** — niveles 0.236/0.382/0.500/0.618/0.786, `GetNearestLevel(price, tolerance%)`, sliding window
- [x] **LinearRegressionIndicator** — mínimos cuadrados (Slope, R², proyección), R² > 0.5 para confirmar tendencia
- [x] **IndicatorFactory** actualizado — Fibonacci + LinearRegression registrados
- [x] **DefaultTradingStrategy multi-indicador** — `CountConfirmations()` con votos de MACD (histograma), Bollinger (bands), EMA/SMA (tendencia), LinReg (slope+R²), Fibonacci (niveles). Mayoría simple requerida.
- [x] **RiskManager esperanza matemática** — `E = (WinRate × AvgWin) − (LossRate × AvgLoss)`. Si E ≤ 0 con ≥10 trades cerrados → bloquea órdenes
- [x] **IPositionRepository.GetTradeStatsAsync** — query eficiente con GroupBy
- [x] **IndicatorConfig** — fábricas `Fibonacci()` y `LinearRegression()` con validación
- [x] **Tests**: FibonacciIndicatorTests (10), LinearRegressionIndicatorTests (10), DefaultTradingStrategy multi-indicador (4), RiskManager esperanza (7) — 31 tests nuevos

**Archivos creados:**
- `src/TradingBot.Application/Strategies/Indicators/FibonacciIndicator.cs`
- `src/TradingBot.Application/Strategies/Indicators/LinearRegressionIndicator.cs`
- `tests/TradingBot.Application.Tests/Indicators/FibonacciIndicatorTests.cs`
- `tests/TradingBot.Application.Tests/Indicators/LinearRegressionIndicatorTests.cs`

**Archivos modificados:**
- `src/TradingBot.Application/Strategies/DefaultTradingStrategy.cs` — `CountConfirmations()`, `BuildSnapshot()` con confirmación
- `src/TradingBot.Application/Strategies/Indicators/IndicatorFactory.cs` — Fibonacci + LinearRegression
- `src/TradingBot.Application/RiskManagement/RiskManager.cs` — `GetMathematicalExpectancyAsync()`, validación #4
- `src/TradingBot.Core/Interfaces/Repositories/IPositionRepository.cs` — `GetTradeStatsAsync`
- `src/TradingBot.Infrastructure/Persistence/Repositories/PositionRepository.cs` — implementación
- `src/TradingBot.Core/ValueObjects/IndicatorConfig.cs` — fábricas + validaciones Fibonacci/LinReg
- `src/TradingBot.Core/Enums/IndicatorType.cs` — `Fibonacci`, `LinearRegression`
- `tests/TradingBot.Application.Tests/Strategies/DefaultTradingStrategyTests.cs` — 4 tests multi-indicador
- `tests/TradingBot.Application.Tests/RiskManagement/RiskManagerTests.cs` — 7 tests esperanza

### Paso D+ — Persistencia de perfil de optimización ✅

- [x] `SavedParameterRange` value object — Name/Min/Max/Step
- [x] `TradingStrategy.UpdateOptimizationRanges()` — guarda rangos para reutilización
- [x] EF Core: columna `SavedOptimizationRanges` (jsonb) con default `[]`
- [x] Migración: `AddOptimizationRanges`
- [x] MediatR: `SaveOptimizationProfileCommand`
- [x] API: `PUT /api/strategies/{id}/optimization-profile`
- [x] Optimizer.razor: carga automática de rangos guardados + botón 💾 Guardar perfil
- [x] Nuevos parámetros optimizables: `maxDailyLossUsdt`, `maxOrderAmountUsdt`, `amountUsdt`
- [x] `ApplyParameters` extendido: soporta risk config completo + monto de reglas

**Archivos creados:**
- `src/TradingBot.Core/ValueObjects/SavedParameterRange.cs`
- `src/TradingBot.Application/Commands/Strategies/SaveOptimizationProfileCommand.cs`
- Migración `AddOptimizationRanges`

**Archivos modificados:**
- `src/TradingBot.Core/Entities/TradingStrategy.cs` — `_savedOptimizationRanges`, `UpdateOptimizationRanges()`
- `src/TradingBot.Infrastructure/Persistence/Configurations/TradingStrategyConfiguration.cs` — jsonb column
- `src/TradingBot.API/Controllers/StrategiesController.cs` — endpoint optimization-profile
- `src/TradingBot.API/Dtos/Dtos.cs` — `SavedParameterRangeDto`, `StrategyDto` expandido
- `src/TradingBot.Application/Backtesting/OptimizationEngine.cs` — `ApplyParameters` con maxDailyLoss/maxOrder/amountUsdt
- `src/TradingBot.Frontend/Models/Dtos.cs` — DTOs
- `src/TradingBot.Frontend/Services/TradingApiClient.cs` — `SaveOptimizationProfileAsync`
- `src/TradingBot.Frontend/Pages/Optimizer.razor` — load/save perfil + nuevos parámetros

---

## 📋 Paso E — Hardening pre-Testnet (resiliencia y seguridad) ✅

> **Objetivo**: Corregir bugs críticos y agregar capas de seguridad antes de operar contra Binance Testnet.

### Fix 1: Notificaciones SignalR de órdenes ejecutadas ✅

**Problema**: `NotifyOrderExecutedAsync` nunca se invocaba — el frontend no se enteraba de órdenes vía SignalR.

- [x] `StrategyEngine.ProcessSingleTickAsync` → llama `NotifyOrderExecutedAsync` al colocar orden de entrada exitosa
- [x] Llama `NotifyAlertAsync` cuando una orden es rechazada por el RiskManager
- [x] Llama `NotifyOrderExecutedAsync` al colocar órdenes de salida (stop-loss/take-profit)

### Fix 2: Kill Switch global + límites de pérdida global ✅

**Problema**: `MaxDailyLossUsdt` solo era por estrategia. 5 estrategias × $500 = $2500 de pérdida sin bloqueo.

- [x] `GlobalRiskSettings` — nuevo options class (`GlobalRisk:MaxDailyLossUsdt`, `GlobalRisk:MaxGlobalOpenPositions`)
- [x] `RiskManager` — validaciones #4 (pérdida diaria global) y #5 (posiciones abiertas globales) antes de esperanza matemática
- [x] `appsettings.json` — sección `GlobalRisk` con defaults (`MaxDailyLossUsdt: 1000`, `MaxGlobalOpenPositions: 20`)
- [x] Log level `Critical` con emoji 🛑 para el kill switch global
- [x] `AddApplication(IConfiguration?)` — acepta configuración opcional (backward compatible)

### Fix 3: Dispose CancellationTokenSource en runners ✅

**Problema**: `CancellationTokenSource.CreateLinkedTokenSource` nunca se disponía → memory leak.

- [x] `StrategyRunnerState` implementa `IDisposable`
- [x] `StopAsync` dispone todos los runners después de cancelarlos
- [x] `ReloadStrategyAsync` dispone el runner al removerlo

### Fix 4: HandlePositionAsync soporta ambos lados (Buy y Sell) ✅

**Problema**: Solo buscaba posiciones `Buy` para cerrar con `Sell`. Shorts nunca se cerraban.

- [x] Reescrito para buscar posiciones del **lado opuesto** a la orden
- [x] Si encuentra posición opuesta → cierra. Si no → abre nueva posición
- [x] Logging del PnL al cerrar posición

### Fix 5: Tick Watchdog ✅

**Problema**: Si el WebSocket se desconectaba silenciosamente, nadie alertaba.

- [x] `RunTickWatchdogAsync` — loop cada 60 segundos, verifica que cada estrategia reciba ticks
- [x] Si una estrategia lleva >5 minutos sin ticks → log `Warning` + alerta SignalR al frontend
- [x] Arranca automáticamente en `ExecuteAsync` como fire-and-forget

### Fix 6: Evaluación de posiciones abiertas al reiniciar ✅

**Problema**: Si se cortaba la energía con posiciones abiertas y el precio se movía mucho, al reiniciar las posiciones quedaban desprotegidas hasta que los indicadores se recalentaban.

- [x] `EvaluateOpenPositionsOnStartupAsync` — al arrancar cada runner, consulta precio actual y evalúa stop-loss/take-profit inmediatamente
- [x] Cierra posiciones que hayan cruzado límites durante el downtime
- [x] Notifica al frontend vía SignalR si cierra alguna posición

**Archivos creados:**
- `src/TradingBot.Application/RiskManagement/GlobalRiskSettings.cs`

**Archivos modificados:**
- `src/TradingBot.Application/Strategies/StrategyEngine.cs` — 6 cambios: notificaciones, watchdog, startup eval, dispose
- `src/TradingBot.Application/RiskManagement/RiskManager.cs` — validaciones globales #4 y #5
- `src/TradingBot.Application/Services/OrderService.cs` — `HandlePositionAsync` simétrico
- `src/TradingBot.Application/ApplicationServiceExtensions.cs` — `IConfiguration` + `GlobalRiskSettings` binding
- `src/TradingBot.API/Program.cs` — pasa `builder.Configuration` a `AddApplication`
- `src/TradingBot.API/appsettings.json` — sección `GlobalRisk`
- `tests/TradingBot.Application.Tests/RiskManagement/RiskManagerTests.cs` — actualizado para `IOptions<GlobalRiskSettings>`

---

## 📋 Paso F — Mejoras pre-Testnet (estrategias, visual, tests) ✅

> **Objetivo**: Implementar mejoras de calidad antes de operar contra Binance Testnet.

### Mejora 1: Signal Cooldown ✅

**Problema**: Si RSI cruza 30 varias veces rápido, podía generar múltiples órdenes en ráfaga.

- [x] `DefaultTradingStrategy._lastSignalAt` — tracking del timestamp de la última señal
- [x] `SignalCooldown = 1 minuto` — tiempo mínimo entre señales consecutivas
- [x] Se aplica DESPUÉS del cruce RSI pero ANTES de la confirmación multi-indicador
- [x] Se resetea en `Reset()` y `RebuildIndicators()`
- [x] Tests: `ProcessTickAsync_WhenSignalWithinCooldown_SuppressesSecondSignal` + `ProcessTickAsync_WhenSignalAfterCooldown_GeneratesSignal`

### Mejora 2: Dashboard con P&L, señales y órdenes en tiempo real ✅

**Problema**: Dashboard solo mostraba ticks. No P&L, no señales, no órdenes, no toast notifications.

- [x] **P&L card global** — suma diaria realizada + no realizada de todas las estrategias
- [x] **P&L por estrategia** — cards compactas con hoy/total/open por estrategia
- [x] **Señales recientes** — panel con últimas 10 señales vía SignalR `OnSignalGenerated`
- [x] **Órdenes recientes** — panel con últimas 10 órdenes vía SignalR `OnOrderExecuted`
- [x] **Toast notifications** — alertas flotantes con auto-dismiss (8s) para señales, órdenes y alertas
- [x] **Auto-refresh** — P&L y status se refrescan cada 30 segundos via `Timer`
- [x] Layout reorganizado: 4 cards top (Motor, WS, Estrategias, P&L) + 2 columnas (señales/órdenes) + ticks + tabla estrategias

### Mejora 3: Tests para Global Risk Limits ✅

- [x] `ValidateOrder_WhenGlobalDailyLossExceeded_ReturnsKillSwitch` — simula pérdida global > $500
- [x] `ValidateOrder_WhenGlobalOpenPositionsExceeded_ReturnsLimitExceeded` — simula 2 posiciones vs límite de 2
- [x] `ValidateOrder_WhenGlobalLimitsDisabled_Passes` — verifica que 0 = deshabilitado

**Archivos modificados:**
- `src/TradingBot.Application/Strategies/DefaultTradingStrategy.cs` — `_lastSignalAt`, `SignalCooldown`, cooldown check en `EvaluateSignal`
- `src/TradingBot.Frontend/Pages/Home.razor` — Dashboard completo con P&L, señales, órdenes, toasts
- `tests/TradingBot.Application.Tests/Strategies/DefaultTradingStrategyTests.cs` — 2 tests cooldown + `CreateTick` con timestamp
- `tests/TradingBot.Application.Tests/RiskManagement/RiskManagerTests.cs` — 3 tests global risk + helpers `CreateClosedPosition`/`CreateOpenPosition`

### Mejora 4: Detección de desconexión de internet en Dashboard ✅

**Problema**: Si el usuario pierde internet, la card "Binance WS" seguía mostrando "🟢 OK" y los ticks viejos seguían visibles.

**Causa raíz**: 3 bugs en `Home.razor`:
1. `LoadStatusAsync` no limpiaba `systemStatus` al fallar → la card usaba datos obsoletos
2. La card "Binance WS" solo miraba `systemStatus.IsConnected` (dato del backend), ignoraba el estado de SignalR
3. Los ticks no se limpiaban al perder conexión SignalR

**Correcciones:**
- [x] `LoadStatusAsync` → al fallar limpia `systemStatus = null` y `pnlSummaries = null`. Contador de fallos consecutivos para mensajes escalados
- [x] **Banner de desconexión** — franja roja visible arriba cuando API o SignalR están caídos: "⛔ Sin conexión — los datos pueden estar desactualizados"
- [x] **Card "Conexión" triple-check** — muestra la peor de 3 señales: API reachable, SignalR connected, Binance WS connected (backend). Línea de detalle: `API: ✓ | SR: ✓ | WS: ✓`
- [x] **SignalR `Closed`** → limpia `recentTicks` + toast "❌ SignalR desconectado — los ticks se detendrán"
- [x] **SignalR `Reconnecting`** → toast "🔄 SignalR reconectando…"
- [x] **SignalR `Reconnected`** → toast "✅ SignalR reconectado"
- [x] **Ticks section** → muestra mensaje "⚠ Sin conexión SignalR" cuando `hubState != "Connected"` en vez de tabla vacía
- [x] **Botón Pausar/Reanudar** → `disabled` cuando API inalcanzable
- [x] **Auto-refresh** cada 15 segundos (antes 30) para detectar desconexiones más rápido
- [x] `LoadPnLAsync` no intenta si `apiReachable == false` (evita request redundante)

---

## 📋 Pasos siguientes — Próximo: Paso 15

### Paso 15 — Ejecución real de órdenes (Binance Testnet)

- `OrderService.ExecuteRealOrderAsync` → Binance.Net REST `PlaceOrderAsync`
- Sincronización de estado de órdenes (`SyncOrderStatusAsync`)
- Webhooks o polling para actualizaciones de estado
- Tests de integración con Binance Testnet (requiere API keys)

### Paso 16 — Notificaciones

- Telegram Bot para alertas de órdenes y señales
- Configuración por estrategia

---

## 🔧 Notas técnicas para el próximo chat

### Archivos creados en Paso 11

1. **`tests/TradingBot.Application.Tests/Rules/RuleEngineTests.cs`** — 33 tests: EvaluateAsync (AND/OR/NOT, comparadores, snapshot parsing, múltiples reglas) + EvaluateExitRulesAsync (stop-loss, take-profit long/short, exit rules, disabled rules)
2. **`tests/TradingBot.Application.Tests/Strategies/DefaultTradingStrategyTests.cs`** — 11 tests: inicialización, señales RSI oversold→Buy / overbought→Sell, zona neutra, snapshot, cruce único, reset, reload
3. **`tests/TradingBot.Application.Tests/Services/OrderServiceTests.cs`** — 13 tests: paper trade flow completo (fill, limit, market fail, positions), live submit, cancel, sync, open orders
4. **`tests/TradingBot.Application.Tests/Commands/IndicatorAndRuleCommandHandlerTests.cs`** — 13 tests: 4 handlers (Add/Remove Indicator, Add/Remove Rule) con not found, CRUD válido, errores de validación

### Archivos que fueron modificados fuera de lo documentado en PROJECT.md

1. **`src/TradingBot.Infrastructure/Persistence/TradingBotDbContext.cs`** — Agregado `FixNewOwnedEntitiesTrackedAsModified()` en `SaveChangesAsync`
2. **`src/TradingBot.API/Program.cs`** — Líneas 48-52: configuración de `JsonOptions` para `IResult` (minimal API JSON)
3. **`src/TradingBot.API/Middleware/ErrorHandlingMiddleware.cs`** — `IsProduction()` en vez de `IsDevelopment()` para error detail
4. **`tests/TradingBot.Integration.Tests/Controllers/ControllerTests.cs`** — Reescrito completo: `SharedFactoryCollection`, remoción agresiva de EF services, `IStrategyEngine` singleton mock
5. **2 migraciones nuevas**: `UseXminConcurrencyToken` + `FixVersionColumnType` (Version de bigint a int)
6. **`src/TradingBot.Infrastructure/Persistence/Configurations/TradingStrategyConfiguration.cs`** — `Version` como `IsConcurrencyToken()` (sin `UseXminForConcurrency`)
7. **`src/TradingBot.API/Dtos/Dtos.cs`** — Creado `StrategyEngineStatusDto` con `FromDomain()` (Symbol como string), `SystemStatusDto` ahora usa este DTO
8. **`src/TradingBot.API/Controllers/SystemController.cs`** — `GetStatus` mapea `StrategyEngineStatus` → `StrategyEngineStatusDto`
9. **`src/TradingBot.Application/Strategies/StrategyEngine.cs`** — `IsRunning` cambiado a `!_isPaused && !_runners.IsEmpty`

### Convenciones establecidas

- **Enums en JSON**: siempre como strings (`JsonStringEnumConverter` en MVC + minimal API)
- **Error handling**: `ErrorHandlingMiddleware` muestra detalles en non-Production
- **Tests de integración**: `[Collection(nameof(SharedFactoryCollection))]` para compartir factory
- **EF Core owned entities**: siempre verificar `FixNewOwnedEntitiesTrackedAsModified()` si hay `OwnsMany`
- **Snapshot parsing locale**: `RuleEngine.ParseFromSnapshot` usa `decimal.TryParse` sin `CultureInfo.InvariantCulture` — en tests, generar snapshots con interpolación `$"RSI(14)={value:F4}"` (no hardcodear dots) para que coincidan con el locale del sistema
- **Tests Application**: `InternalsVisibleTo` ya configurado en `TradingBot.Application.csproj` para `TradingBot.Application.Tests` y `DynamicProxyGenAssembly2` (NSubstitute)
- **`decimal` en `[InlineData]`**: no permitido en atributos C# → usar `TheoryData<>` con `[MemberData]` y parámetros `double`, castear a `decimal` en el método

### Comandos útiles

```powershell
# Build
dotnet build

# Tests
dotnet test

# Migraciones
cd src/TradingBot.API
dotnet ef database update --project ../TradingBot.Infrastructure
dotnet ef migrations add <NombreMigracion> --project ../TradingBot.Infrastructure

# Docker
docker compose up -d
docker compose down
docker compose ps
```
