# TradingBot — Pasos a Seguir

> **Contexto para Copilot en un nuevo chat.**
> Este archivo describe el estado actual del proyecto y los pasos concretos para continuar.
> Leer junto con `.github/PROJECT.md` y `.github/copilot-instructions.md`.

---

## 📊 Estado actual verificado (2026-03-16)

| Métrica | Valor |
|---------|-------|
| Build | ✅ Compilación correcta (8 proyectos) |
| Tests | ✅ **358/358 passing** — Core (74) + Application (276) + Integration (8) |
| TFM | .NET 10 / C# 14 |
| Migraciones EF Core | 6 aplicadas: `InitialCreate`, `UseXminConcurrencyToken`, `FixVersionColumnType`, `AddOptimizationRanges`, `AddPositionPeakPriceTracking`, `AddOrderFeeColumn` |
| Multi-project launch | `TradingBot.slnLaunch.user` con API + Frontend |
| **E2E** | ✅ Probado: CRUD estrategias, indicadores, reglas, activar/desactivar, eliminar, SignalR ticks |

### Capas completadas

| Capa | Estado | Archivos clave |
|------|--------|----------------|
| **Core** | ✅ 11 enums, 12 VOs, 4 entidades, 10 eventos, 19 interfaces | `src/TradingBot.Core/` |
| **Infrastructure** | ✅ EF Core + Npgsql, 3 repos, Binance.Net WS+REST, Redis cache, Serilog, ExchangeInfoService, BinanceOrderFilter, UserDataStreamService, BinanceAccountService | `src/TradingBot.Infrastructure/` |
| **Application** | ✅ 14 MediatR handlers, StrategyEngine (BackgroundService), RuleEngine, RiskManager (esperanza matemática + portafolio), OrderService (+ exchange filter + fees), 9 indicadores, multi-indicator confirmation, MarketRegimeDetector, PositionSizer, FeeAndSlippageCalculator, TradingMetrics (OpenTelemetry) | `src/TradingBot.Application/` |
| **API** | ✅ 5 controllers (30+ endpoints), SignalR Hub, ErrorHandling middleware, SignalRTradingNotifier, 4 strategy templates, API Key Auth, Rate Limiting, Health Checks | `src/TradingBot.API/` |
| **Frontend** | ✅ 7 páginas Blazor WASM (Dashboard, Strategies, StrategyDetail, Orders, Positions, Backtest, Optimizer), TradingApiClient, SignalR, ApiKeyDelegatingHandler | `src/TradingBot.Frontend/` |
| **Tests** | ✅ Core (74), Application (276), Integration (8) | `tests/` |

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

## 🗺️ Roadmap hacia producción real

> **Objetivo**: operar con dinero real de forma segura y rentable.
> Las etapas son secuenciales — no pasar a la siguiente sin completar la anterior.
> Cada etapa tiene criterio de salida (`✅ Done when`) antes de continuar.

---

## 🔴 ETAPA 1 — Bloqueantes Binance (sin esto las órdenes reales fallan)

> **Criterio de salida**: todas las órdenes en Testnet se ejecutan sin errores de filtro.

### Paso 15 — Filtros de Exchange de Binance ✅

**Problema**: Binance rechaza órdenes que no cumplen sus filtros por símbolo. Sin esto el 80% de las órdenes reales fallarán.

- [x] **`IExchangeInfoService`** — interfaz en Core para obtener filtros por símbolo
- [x] **`ExchangeSymbolFilters`** — value object en Core con filtros `LOT_SIZE`, `PRICE_FILTER`, `MIN_NOTIONAL`, `MAX_NUM_ORDERS`
- [x] **`ExchangeSymbolFilters.AdjustQuantity/AdjustPrice/ValidateAndAdjust`** — lógica de ajuste y validación directamente en el value object (Core)
  - `stepSize` para cantidad (floor al múltiplo más cercano)
  - `tickSize` para precio (round half-up al múltiplo más cercano)
  - Validación `minQty`, `maxQty`, `minNotional`
- [x] **`BinanceExchangeInfoService`** — implementación en Infrastructure con Redis cache (TTL 1h) + Polly retry
- [x] **`BinanceOrderFilter`** — clase en Infrastructure que delega a `ExchangeSymbolFilters` (sin duplicación de lógica)
- [x] **`BinanceSpotOrderExecutor`** — aplica filtros para órdenes live antes de enviar a Binance REST
- [x] **`OrderService` — validación de filtros para TODAS las órdenes** (Paper + Live) como paso previo a ejecución
  - Inyecta `IExchangeInfoService`
  - Valida después del RiskManager, antes de persistir y ejecutar
  - Degradación graceful: si filtros no disponibles, continúa sin validar
- [x] **Tests `ExchangeSymbolFiltersTests`** (20): AdjustQuantity (4), AdjustPrice (4), ValidateAndAdjust (8), Precision (4)
- [x] **Tests `OrderServiceTests`** (5 nuevos): filtros unavailable, minQty, maxQty, notional, filters pass
- [x] **Tests totales: 312/312 passing** — Core (67) + Application (237) + Integration (8)

**Archivos creados:**
- `tests/TradingBot.Core.Tests/ValueObjects/ExchangeSymbolFiltersTests.cs`

**Archivos modificados:**
- `src/TradingBot.Core/ValueObjects/ExchangeSymbolFilters.cs` — `AdjustQuantity()`, `AdjustPrice()`, `ValidateAndAdjust()`
- `src/TradingBot.Infrastructure/Binance/BinanceOrderFilter.cs` — delegación a `ExchangeSymbolFilters`
- `src/TradingBot.Application/Services/OrderService.cs` — inyección de `IExchangeInfoService` + `ValidateExchangeFiltersAsync`
- `tests/TradingBot.Application.Tests/Services/OrderServiceTests.cs` — `IExchangeInfoService` mock + 5 tests nuevos

### Paso 16 — Balance real-time y sincronización de cuenta ✅ (pre-implementado)

**Problema**: el bot puede intentar comprar más de lo que hay en la cuenta.

- [x] **`IAccountService`** — interfaz con `GetAvailableBalanceAsync(asset)` y `GetAccountSnapshotAsync()`
- [x] **`BinanceAccountService`** — implementación con Binance.Net REST + caché 5 segundos
- [x] **`RiskManager`** — validación #0 (antes de todo): `orderAmountUsdt <= availableUsdt * 0.95` (FeeBuffer 5%)
- [x] **Actualizar balance en cache** tras cada orden ejecutada (`InvalidateCacheAsync` en `OrderService`)
- [x] **Frontend**: card de balance en Dashboard (USDT disponible, BTC, ETH, etc.) — implementado en `Home.razor` con `accountBalances`
- [x] **Tests**: `RiskManager` con balance insuficiente — `ValidateOrder_WhenLiveBalanceInsufficient_ReturnsRiskLimitExceeded` + `ValidateOrder_WhenLiveBalanceSufficient_Passes`

### Paso 17 — User Data Stream + sincronización de órdenes ✅ (pre-implementado)

**Problema**: sin esto el bot no sabe si una orden fue filled/cancelled en Binance sin hacer polling constante → riesgo de rate limit y estado inconsistente.

- [x] **`UserDataStreamService`** — WebSocket con `SubscribeToUserDataUpdatesAsync`
  - Evento `executionReport` → actualiza `Order` en BD + cache
  - Evento `outboundAccountPosition` → invalida caché de balance
  - CryptoExchange.Net gestiona listenKey y renovación automáticamente
  - Reconexión automática con backoff exponencial
- [x] **`IHostedService`** — arranca automáticamente con el host
- [x] **`OrderService.SyncOrderStatusAsync`** — polling de respaldo para órdenes `Submitted`
- [x] **Partial fills**: `PartialFill()` en la máquina de estados de `Order`
- [x] **`OrderRepository`**: `GetPendingSyncAsync` — órdenes `Submitted`/`PartiallyFilled` pendientes de sincronización
- [ ] **Tests**: `UserDataStreamServiceTests` — diferido (requiere mocking de WebSocket infrastructure de CryptoExchange.Net)

---

## 🟠 ETAPA 2 — Fidelidad de simulación (Backtest y Paper Trading realistas)

> **Criterio de salida**: backtest con fees/slippage muestra P&L ≤ 30% del P&L sin fees. Si la estrategia sigue siendo rentable, continuar.

### Paso 18 — Fees y slippage en Paper Trading y Backtest ✅ (pre-implementado)

**Problema**: sin fees, el backtest sobreestima el P&L en ~0.2% por trade. Con 200 trades/mes esto puede convertir una estrategia perdedora en "ganadora" en papel.

- [x] **`TradingFeeConfig`** — `IOptions<TradingFeeConfig>` con `MakerFeePercent`, `TakerFeePercent`, `UseBnbDiscount`, `SlippagePercent`
- [x] **`FeeAndSlippageCalculator`** — `ApplySlippage`, `CalculateFee`, `QuantityAfterFee`, `CalculateRoundTripImpact`
- [x] **`OrderService.SimulatePaperTradeAsync`** — aplica slippage al precio + calcula fee (maker/taker)
- [x] **`BacktestEngine`** — aplica fees + slippage en cada trade usando `CalculateRoundTripImpact`
- [x] **`BacktestResult`** — campos `GrossPnL`, `TotalFeesUsdt`, `TotalSlippageUsdt`, `TotalPnL` (neto)
- [x] **Frontend Backtest**: muestra P&L bruto vs neto + fees + slippage con cards separadas
- [x] **Tests**: `BacktestMetricsTests.Calculate_NetPnLLessThanGross_WhenFeesApplied` — verifica P&L neto < bruto

### Paso 19 — Métricas de calidad en el Optimizador ✅

**Problema**: el optimizador rankeaba por P&L bruto → overfitting garantizado. El mejor resultado histórico rara vez es el mejor en live.

- [x] **`BacktestMetrics`** — Sharpe, Sortino, Calmar, ProfitFactor, MaxConsecutiveLosses/Wins, Expectancy
- [x] **`BacktestMetrics.Calculate(trades)`** — cálculo automático desde lista de trades
- [x] **`OptimizationRunSummary`** — incluye `BacktestMetrics` por cada combinación
- [x] **`OptimizationRankBy` enum** — `PnL | SharpeRatio | SortinoRatio | CalmarRatio | ProfitFactor`
- [x] **`OptimizationEngine.RunAsync`** — ranking configurable con `rankBy` parameter + `GetRankingValue()`
- [x] **`OptimizationResult`** — incluye `RankedBy` para saber qué métrica se usó
- [x] **`RunOptimizationCommand`** — acepta `RankBy` parameter
- [x] **API**: `RunOptimizationRequest.RankBy` → `BacktestController` parsea y pasa al command
- [x] **DTOs**: `OptimizationRunSummaryDto.Metrics`, `OptimizationResultDto.RankedBy`, `BacktestMetricsDto`
- [x] **Frontend Optimizer**: selector de métrica de ranking, columnas Sharpe/Sortino/PF en tabla, métricas en Top 3 cards
- [ ] **Walk-forward analysis** — dividir klines en 70% train / 30% test (diferido a post-Testnet)
- [x] **Tests**: `BacktestMetricsTests` (11 tests) — Sharpe, Sortino, Calmar, ProfitFactor, Expectancy, MaxConsecutive, NoTrades, AllWins, AllLosses, NetPnL<Gross, KnownReturns

---

## 🟡 ETAPA 3 — Calidad de señales (estrategias más robustas) ✅

> **Criterio de salida**: estrategia probada en Testnet real durante 2 semanas con Sharpe > 1.0 y max drawdown < 15%.

### Paso 20 — Detector de régimen de mercado ✅

**Problema**: RSI en oversold/overbought falla en tendencias fuertes. BTC en bull run 2021 tuvo RSI > 70 durante semanas → el bot habría vendido continuamente.

- [x] **`AdxIndicator`** — Average Directional Index: `ADX > 25` = tendencia, `ADX < 20` = ranging
  - `+DI` y `-DI` para dirección de la tendencia
  - `IndicatorType.ADX` + `IndicatorFactory` + `IndicatorConfig.Adx()`
- [x] **`MarketRegime` enum** — `Trending | Ranging | HighVolatility | Unknown`
- [x] **`MarketRegimeDetector`** — combina ADX + Bollinger BandWidth + ATR para clasificar régimen
- [x] **`DefaultTradingStrategy`** — filtrar señales según régimen:
  - `Ranging` → habilitar señales RSI oversold/overbought
  - `Trending` → solo señales en dirección de la tendencia (+DI/-DI de ADX)
  - `HighVolatility` → no operar (señales suprimidas)
- [x] **Frontend StrategyDetail**: card de régimen actual por símbolo (📈 Trending / ↔️ Ranging / ⚡ HighVolatility / ❓ Unknown) con métricas del runner
- [x] **Tests**: `AdxIndicatorTests`, `MarketRegimeDetectorTests`, `DefaultTradingStrategy` por régimen

### Paso 21 — Position sizing dinámico (ATR-based) ✅

**Problema**: `amountUsdt` fijo ignora la volatilidad. En días de alta volatilidad el stop-loss salta con mayor frecuencia → pérdidas innecesarias.

- [x] **`AtrIndicator`** — Average True Range: medida de volatilidad del mercado
  - `IndicatorType.ATR` + factory + config
- [x] **`PositionSizer`** — calcula tamaño óptimo de posición:
  ```
  riskAmount = accountBalance * riskPercentPerTrade  (ej: 1%)
  stopDistance = ATR * atrMultiplier                 (ej: 2x ATR)
  positionSize = riskAmount / stopDistance
  ```
- [x] **`RiskConfig`** — `RiskPercentPerTrade` (default 1%), `AtrMultiplier` (default 2.0), `UseAtrSizing = false` — ya implementado
- [x] **`StrategyEngine`** — si `UseAtrSizing = true`, calcula qty con `PositionSizer` usando balance de `IAccountService` + ATR de `SignalGeneratedEvent.AtrValue`
- [x] **Stop-loss dinámico** basado en ATR: `RuleEngine.EvaluateExitRulesAsync` recibe `atrValue` → calcula `stopLossPrice = entryPrice ∓ (ATR × atrMultiplier)` por lado
  - Long: SL = entry − (ATR × mult), triggerea si price ≤ SL
  - Short: SL = entry + (ATR × mult), triggerea si price ≥ SL
  - Fallback a stop-loss porcentual si ATR no disponible
  - Take-profit siempre porcentual
- [x] **Tests**: `AtrIndicatorTests`, `PositionSizerTests`, **5 nuevos tests ATR stop-loss** (long triggered, not triggered, short triggered, fallback sin ATR, take-profit porcentual)

#### Cambios realizados para completar Etapa 3

**Archivos modificados:**
- `src/TradingBot.Core/Events/SignalGeneratedEvent.cs` — `decimal? AtrValue` para propagar ATR a través del pipeline
- `src/TradingBot.Core/Interfaces/Trading/ITradingStrategy.cs` — `CurrentRegime` y `CurrentAtrValue` propiedades
- `src/TradingBot.Core/Interfaces/Services/IStrategyEngine.cs` — `StrategyEngineStatus` con `MarketRegime CurrentRegime`
- `src/TradingBot.Core/Interfaces/Services/IRuleEngine.cs` — `EvaluateExitRulesAsync` con `decimal? atrValue`
- `src/TradingBot.Application/Strategies/DefaultTradingStrategy.cs` — implementa `CurrentRegime`, `CurrentAtrValue`, propaga ATR en señal
- `src/TradingBot.Application/Strategies/StrategyEngine.cs` — integra `PositionSizer` con `IAccountService`, pasa ATR a exit rules, expone régimen en status
- `src/TradingBot.Application/Rules/RuleEngine.cs` — stop-loss dinámico ATR vs porcentual, take-profit siempre porcentual
- `src/TradingBot.API/Dtos/Dtos.cs` — `StrategyEngineStatusDto` con `CurrentRegime`
- `src/TradingBot.Frontend/Models/Dtos.cs` — `StrategyEngineStatusDto` con `CurrentRegime`
- `src/TradingBot.Frontend/Pages/StrategyDetail.razor` — card de régimen con icono/color/descripción
- `tests/TradingBot.Application.Tests/Rules/RuleEngineTests.cs` — 5 tests ATR stop-loss + helper `CreateAtrStrategy`

---

## 🟢 ETAPA 4 — Gestión de riesgo de portafolio

> **Criterio de salida**: nunca más de X% del capital expuesto en una dirección. Kill switch de portafolio probado.

### Paso 22 — Correlación y exposición de portafolio ✅

**Problema**: 5 estrategias con BTCUSDT Long operando simultáneamente = misma exposición x5. Una caída brusca de BTC limpia todas las posiciones a la vez.

- [x] **`PortfolioRiskManager`** — análisis de exposición neta:
  - `GetExposureBySymbolAsync()` — exposición larga y corta por símbolo
  - `GetPortfolioExposureAsync()` — exposición total larga vs corta + neta
  - `ValidateExposureAsync()` — valida límites Long/Short/concentración por símbolo
- [x] **`GlobalRiskSettings`** — agregados: `MaxPortfolioLongExposureUsdt`, `MaxPortfolioShortExposureUsdt`, `MaxExposurePerSymbolPercent`, `MaxAccountDrawdownPercent`
- [x] **`RiskManager`** — integrado `PortfolioRiskManager` como validación #6 (antes de esperanza matemática)
- [x] **Circuit breaker de drawdown de cuenta** — `CheckAccountDrawdownAsync()`: si balance cae > X% en el día → kill switch + log crítico
- [x] **`IRiskManager`** — extendida interfaz con `CheckAccountDrawdownAsync`, `GetPortfolioExposureAsync`, `GetExposureBySymbolAsync`
- [x] **API**: `GET /api/system/exposure` — devuelve exposición total + por símbolo + drawdown
- [x] **DTOs**: `PortfolioExposureDto`, `SymbolExposureDto` (API + Frontend)
- [x] **Frontend Dashboard**: card de exposición del portafolio (Long/Short/Neto por símbolo, drawdown badge)
- [x] **DI**: `ApplicationServiceExtensions` parsea los nuevos settings de `GlobalRisk`
- [x] **Tests**: `PortfolioRiskManagerTests` (11 tests): exposición vacía, mixta, por símbolo, límites Long/Short/concentración, límites deshabilitados, Sell vs Long limit, static helper

**Archivos creados:**
- `src/TradingBot.Application/RiskManagement/PortfolioRiskManager.cs`
- `tests/TradingBot.Application.Tests/RiskManagement/PortfolioRiskManagerTests.cs`

**Archivos modificados:**
- `src/TradingBot.Application/RiskManagement/GlobalRiskSettings.cs` — 4 propiedades nuevas
- `src/TradingBot.Application/RiskManagement/RiskManager.cs` — validación #6 portafolio + `CheckAccountDrawdownAsync` + `GetPortfolioExposureAsync` + `GetExposureBySymbolAsync`
- `src/TradingBot.Core/Interfaces/Services/IRiskManager.cs` — 3 métodos nuevos
- `src/TradingBot.Application/ApplicationServiceExtensions.cs` — parsing de nuevos settings
- `src/TradingBot.API/Controllers/SystemController.cs` — endpoint `/api/system/exposure`
- `src/TradingBot.API/Dtos/Dtos.cs` — `PortfolioExposureDto`, `SymbolExposureDto`
- `src/TradingBot.Frontend/Models/Dtos.cs` — DTOs espejo
- `src/TradingBot.Frontend/Services/TradingApiClient.cs` — `GetPortfolioExposureAsync`
- `src/TradingBot.Frontend/Pages/Home.razor` — card de exposición con long/short/neto/drawdown

---

## 🔵 ETAPA 5 — Ejecución real en Testnet

> **Criterio de salida**: bot operando en Testnet 1 semana sin errores.

### Paso 23 — Ejecución real de órdenes (Binance Testnet) ✅ (pre-implementado + hardening)

- [x] **`BinanceSpotOrderExecutor.PlaceOrderAsync`** — Binance.Net REST con Polly retry (3 intentos, backoff exponencial + jitter, timeout 15s)
- [x] **Filtros de exchange** — aplica `LOT_SIZE`, `PRICE_FILTER`, `MIN_NOTIONAL` antes de enviar (vía `BinanceOrderFilter`)
- [x] **Manejo de errores específicos de Binance**:
  - `-1013` (filter failure) → fail fast, no reintentar
  - `-2010` (insufficient balance) → fail fast
  - `-1021` (timestamp) → fail fast
  - `-1003` / `-1015` (rate limit) → log warning, retry con backoff
  - `BinanceNonRetryableException` — clasificación de errores retryables vs determinísticos
- [x] **`OrderService.ExecuteSpotOrderAsync`** — Submit → Fill/PartialFill + HandlePosition + InvalidateBalanceCache
- [x] **`OrderService.SyncOrderStatusAsync`** — polling de fallback para órdenes Submitted
- [x] **`UserDataStreamService`** — WebSocket IHostedService con reconexión automática (fuente primaria)
- [x] **Partial fills** — `Order.PartialFill()` state machine
- [x] **`CancelOrderAsync`** — cancela en Binance primero, luego localmente
- [x] **Modo Testnet/Demo** — `BINANCE_USE_TESTNET=true` / `BINANCE_USE_DEMO=true` vía env vars, switch transparente en `BinanceEnvironment`
- [x] **Logging estructurado** — cada orden enviada/confirmada/rechazada con IDs de Binance
- [ ] **Tests de integración con Binance Testnet** — requiere API keys reales de testnet (manual)

---

## ⚫ ETAPA 6 — Observabilidad y operaciones

> **Criterio de salida**: el bot puede ejecutarse 24/7 sin intervención manual, con alertas ante cualquier anomalía.

### Paso 25 — Health checks y métricas de sistema ✅

- [x] **Health checks** (`IHealthCheck`):
  - `BinanceHealthCheck` — ping a Binance REST API (`/api/v3/ping`)
  - `StrategyEngineHealthCheck` — verifica runners activos y que reciban ticks (threshold 5min)
  - PostgreSQL — via `AspNetCore.HealthChecks.NpgSql` (liveness)
  - Redis — via `AspNetCore.HealthChecks.Redis` (liveness)
  - Endpoint `/health` (todos), `/health/ready` (DB + cache + external), `/health/live` (engine)
  - Respuesta JSON estructurada con `HealthCheckResponseWriter` (status, duration, checks[], data, exception)
- [x] **Métricas con `System.Diagnostics.Metrics`** (`TradingMetrics`):
  - `trading.ticks_processed` — counter por símbolo
  - `trading.signals_generated` — counter por estrategia/símbolo/dirección
  - `trading.orders_placed` — counter por símbolo/side/type/paper
  - `trading.orders_failed` — counter por símbolo/reason
  - `trading.tick_to_order_latency` — histogram en ms
  - `trading.pnl_daily` — observable gauge
  - Registrado como Singleton en DI, compatible con OpenTelemetry
- [x] **Log rotation** — Serilog `RollingInterval.Day`, retención 30 días (ya existente en `Program.cs`)
- [x] **Structured logging** — Serilog con `Enrich.FromLogContext()` + `Enrich.WithMachineName()` + `Application` property

**Archivos creados:**
- `src/TradingBot.API/Health/BinanceHealthCheck.cs`
- `src/TradingBot.API/Health/StrategyEngineHealthCheck.cs`
- `src/TradingBot.API/Health/HealthCheckResponseWriter.cs`
- `src/TradingBot.Application/Diagnostics/TradingMetrics.cs`

**Archivos modificados:**
- `src/TradingBot.API/TradingBot.API.csproj` — `AspNetCore.HealthChecks.NpgSql` + `AspNetCore.HealthChecks.Redis`
- `src/TradingBot.API/Program.cs` — `AddHealthChecks()` + `MapHealthChecks()` 3 endpoints
- `src/TradingBot.Application/ApplicationServiceExtensions.cs` — `TradingMetrics` singleton

### Paso 26 — CI/CD y hardening de producción ✅

- [x] **GitHub Actions** — `.github/workflows/ci.yml`:
  - `dotnet restore` → `dotnet build --configuration Release` → `dotnet test` en cada push/PR
  - PostgreSQL + Redis como services (para integration tests)
  - Publicación de resultados con `dorny/test-reporter`
  - Build de Docker image en merge a master/main
- [x] **Dockerfile** — `src/TradingBot.API/Dockerfile`:
  - Multi-stage build (SDK → Runtime)
  - Capa de caché para `dotnet restore`
  - `HEALTHCHECK` integrado con `curl /health/live`
  - `BINANCE_USE_TESTNET=true` por defecto (seguridad)
- [x] **Docker Compose producción** — `docker-compose.prod.yml`:
  - Servicios: API, PostgreSQL 16, Redis 7, pg-backup
  - CPU/RAM limits (`deploy.resources.limits`)
  - Restart policy `unless-stopped`
  - Volúmenes nombrados (`postgres-data`, `redis-data`, `api-logs`, `postgres-backups`)
  - Variables de entorno desde `.env`
  - Health checks en todos los servicios
- [x] **Backup automático PostgreSQL** — `pg-backup` container: pg_dump diario a las 02:00 UTC, retención 7 días
- [x] **`.env.example`** — template de variables de entorno (sin secrets)
- [x] **`.gitignore`** — `.env` excluido, `.env.example` permitido
- [x] **`appsettings.json`** — nuevos campos de `GlobalRisk` (portafolio)
- [x] **Autenticación API Key** — `ApiKeyAuthenticationHandler` + `X-Api-Key` header:
  - `TRADINGBOT_API_KEY` env var o `Authentication:ApiKey` en config
  - `[Authorize]` en todos los controllers
  - Sin key configurada → acceso libre (desarrollo)
  - `IPostConfigureOptions` para compatibilidad con tests de integración
- [x] **Rate Limiting** — `AddRateLimiter` con Fixed Window (100 req/min por defecto)
- [x] **Frontend API Key** — `ApiKeyDelegatingHandler` inyecta header automáticamente
- [x] **Entidad `Order.Fee`** — campo `decimal Fee` para persistir comisiones (paper + live)
- [x] **Migración `AddOrderFeeColumn`** — columna `Fee` con default 0
- [x] **`StrategyEngine` scope fix** — scope de larga vida por runner (evita dispose prematuro)
- [x] **Lock por estrategia** — `SemaphoreSlim` en tick processing para evitar órdenes duplicadas
- [x] **Trailing stop ATR** — usa peak price (`HighestPriceSinceEntry`/`LowestPriceSinceEntry`) cuando `UseTrailingStop` está activo

**Archivos creados:**
- `.github/workflows/ci.yml`
- `src/TradingBot.API/Dockerfile`
- `docker-compose.prod.yml`
- `.env.example`

---

## 🏁 ETAPA 7 — Activación Live con dinero real

> **Criterio de salida**: protocolo de activación completado, capital mínimo de prueba, monitoreo 24/7 activo.

### Paso 27 — Protocolo de activación gradual

**Regla de oro**: nunca pasar a live sin haber completado todas las etapas anteriores.

- [ ] **Checklist pre-live**:
  - [ ] Testnet operando estable ≥ 2 semanas sin errores críticos
  - [ ] Sharpe Ratio walk-forward > 1.0 en al menos 3 estrategias
  - [ ] Max drawdown en Testnet < 15%
  - [ ] Kill switch global probado (activación manual + automática)
  - [ ] Backup de BD verificado
  - [ ] Filtros Binance probados con todos los símbolos a operar
  - [ ] API Key de autenticación configurada (`TRADINGBOT_API_KEY`)
- [ ] **Activación por fases**:
  1. Fase Alpha: 1 estrategia, 1 símbolo, máx $50 USDT, 2 semanas
  2. Fase Beta: 2-3 estrategias, máx $200 USDT, 1 mes
  3. Fase Producción: escala gradual según performance real
- [ ] **Kill switch manual** desde Frontend
- [ ] **Procedimiento de emergencia** documentado:
  - ¿Qué hacer si el bot ejecuta órdenes erróneas?
  - ¿Cómo cerrar todas las posiciones manualmente desde Binance?
  - ¿Cómo pausar el bot sin perder el estado?

---

## 📊 Resumen de etapas

| Etapa | Descripción | Pasos | Prioridad |
|-------|-------------|-------|-----------|
| 🔴 **1** | Bloqueantes Binance (filtros, balance, User Data Stream) | 15-17 | ✅ COMPLETADA |
| 🟠 **2** | Simulación realista (fees, slippage, métricas Sharpe/Sortino) | 18-19 | ✅ COMPLETADA |
| 🟡 **3** | Calidad de señales (ADX, ATR sizing) | 20-21 | ✅ COMPLETADA |
| 🟢 **4** | Riesgo de portafolio (correlación, circuit breaker) | 22 | ✅ COMPLETADA |
| 🔵 **5** | Ejecución Testnet real | 23 | ✅ COMPLETADA |
| ⚫ **6** | Observabilidad (health checks, CI/CD, auth, rate limiting) | 25-26 | ✅ COMPLETADA |
| 🏁 **7** | Activación Live (protocolo gradual) | 27 | ⏳ SIGUIENTE |

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
