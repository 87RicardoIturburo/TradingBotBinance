# TradingBot — Documentación del Proyecto

## 📌 Descripción General

**TradingBot** es un sistema autónomo de trading para Binance Spot que ejecuta estrategias
y reglas configuradas por el usuario. Opera 24/7 procesando datos de mercado en tiempo
real vía WebSocket, tomando decisiones de compra/venta basadas en indicadores técnicos
y reglas configurables que pueden modificarse **sin reiniciar el sistema** (hot-reload).

| Métrica | Valor |
|---------|-------|
| TFM | .NET 10 / C# 14 |
| Tests | **378/378 passing** — Core (80) + Application (290) + Integration (8) |
| Migraciones EF Core | 13 aplicadas |
| Etapa actual | 🏁 **Completo** — DESIGN-2~6 + IMP-1~9 (excepto IMP-2) implementados |

---

## 🎯 Objetivos del Sistema

| Objetivo | Descripción |
|----------|-------------|
| **Autonomía** | Opera sin intervención humana continua siguiendo reglas configuradas |
| **Tiempo real** | Procesa ticks de mercado con latencia < 100ms |
| **Flexibilidad** | Estrategias y reglas modificables en tiempo de ejecución (hot-reload) |
| **Seguridad** | Gestión de riesgo multi-capa, modo paper trading, kill switch global |
| **Observabilidad** | Dashboard en tiempo real, métricas, health checks, alertas SignalR |

---

## 🏛️ Arquitectura

### Diagrama de capas

```
┌─────────────────────────────────────────────┐
│        Blazor WebAssembly (Frontend)        │
│   Dashboard │ Estrategias │ Backtest │ P&L  │
└──────────────────────┬──────────────────────┘
                  SignalR / HTTP
┌──────────────────────┴──────────────────────┐
│           .NET 10 Web API                   │
│   Controllers │ SignalR Hub │ Auth │ Health │
└──────────────────────┬──────────────────────┘
┌──────────────────────┴──────────────────────┐
│        Application Layer (CQRS)             │
│ StrategyEngine │ RuleEngine │ RiskManager   │
│ OrderService │ BacktestEngine │ Optimizer   │
└──────┬───────────────────────────┬──────────┘
┌──────┴──────┐          ┌─────────┴──────────┐
│ PostgreSQL  │          │    Binance API     │
│  + Redis    │          │  REST + WebSocket  │
└─────────────┘          └────────────────────┘
```

---

## 🧩 Componentes Principales

### Strategy Engine (BackgroundService)

Orquestador central. Por cada estrategia activa arranca un runner con loops paralelos:

- **Kline loop**: velas cerradas → indicadores → señales → reglas de entrada → órdenes. También evalúa reglas de salida basadas en indicadores para posiciones abiertas.
- **Tick loop**: ticks de precio → SL/TP/trailing stop → cierra posiciones. **No alimenta indicadores.**
- **Confirmation loop** (opcional): velas del timeframe superior para Multi-Timeframe Analysis.

### Rule Engine

- Condiciones combinables: AND / OR / NOT con comparadores `<`, `>`, `≤`, `≥`, `=`, `≠`, `CrossAbove`, `CrossBelow`
- Reglas de salida: SL/TP/trailing (tick loop) + reglas custom con indicadores (kline loop)
- Parámetro `evaluateIndicatorRules` separa evaluación tick vs kline

### Risk Manager

- Límites por orden y pérdida diaria (por estrategia y global)
- Exposición de portafolio: Long/Short, concentración por símbolo
- Esperanza matemática: bloquea estrategias con E ≤ 0 (≥10 trades)
- Kill switch global: pérdida diaria total + drawdown de cuenta
- ATR-based position sizing + validación MIN_NOTIONAL para Market orders
- Position sizing conservador (50% del max) cuando ATR no está disponible
- Circuit breaker con auto-reset al inicio del siguiente día UTC

### Indicadores Técnicos (9)

| Indicador | Notas |
|-----------|-------|
| RSI | Oversold/Overbought configurable |
| MACD | Fast/Slow/Signal EMA |
| EMA / SMA | Moving averages |
| Bollinger Bands | Solo genera señales en régimen Ranging (no Trending) |
| ADX | Fuerza de tendencia, +DI/-DI |
| ATR (`IOhlcIndicator`) | True Range real con OHLC: max(H-L, abs(H-prevC), abs(L-prevC)) |
| Fibonacci | Niveles de retroceso 0.236/0.382/0.500/0.618/0.786 |
| Linear Regression | Slope, R², proyección |

### Backtest y Optimización

- Klines históricas con `ProcessKlineAsync`, fees + slippage configurables
- Métricas: Sharpe (anualizado, retornos %), Sortino, Calmar, Profit Factor, Expectancy
- Equity curve con balance real (`initialBalance` + P&L acumulado)
- Optimización cartesiana (máx 500 combinaciones), ranking multi-métrica
- Walk-forward analysis: 70/30 split train/test con detección de overfitting

### Reconciliación y Sincronización

- `BinanceReconciliationWorker`: verifica órdenes pendientes cada 60s contra Binance REST
- Detecta fills y cancelaciones no capturadas por WebSocket
- Fee tracking real con `FeeAsset` (BNB discount) en órdenes Live
- Caché de posiciones abiertas en memoria con TTL 2s para reducir queries a DB

---

## 🗂️ Estructura del proyecto

```
TradingBot/
├── src/
│   ├── TradingBot.API/              # Web API + SignalR Hub + Auth + Health
│   ├── TradingBot.Core/             # Dominio puro
│   │   ├── Common/                  # Result<T>, DomainError, Entity, AggregateRoot
│   │   ├── Enums/ (12)              # OrderSide/Type/Status, StrategyStatus, TradingMode,
│   │   │                            # IndicatorType, RuleType, ConditionOperator, Comparator,
│   │   │                            # ActionType, MarketRegime, CandleInterval
│   │   ├── ValueObjects/ (12)       # Symbol, Price, Quantity, Percentage, RiskConfig,
│   │   │                            # IndicatorConfig, LeafCondition, RuleCondition, RuleAction,
│   │   │                            # SavedParameterRange, AccountBalance, ExchangeSymbolFilters
│   │   ├── Events/ (11)             # DomainEvent, MarketTickReceived, KlineClosed,
│   │   │                            # OrderPlaced/Filled/Cancelled, SignalGenerated,
│   │   │                            # StrategyUpdated/Activated, RiskLimitExceeded
│   │   ├── Entities/ (4)            # TradingStrategy (aggregate), Order, Position, TradingRule
│   │   └── Interfaces/ (23)         # IUnitOfWork, 3 repos, 2 trading, 17 services
│   ├── TradingBot.Application/      # Casos de uso (CQRS con MediatR)
│   │   ├── Strategies/              # StrategyEngine, DefaultTradingStrategy, 9 indicadores
│   │   ├── Rules/                   # RuleEngine
│   │   ├── RiskManagement/          # RiskManager, PortfolioRiskManager, PositionSizer
│   │   ├── Backtesting/             # BacktestEngine, OptimizationEngine, BacktestMetrics
│   │   ├── Services/                # OrderService, StrategyConfigService,
│   │   │                            # BinanceReconciliationWorker, LimitOrderTimeoutWorker
│   │   ├── Commands + Queries/      # 14 MediatR handlers
│   │   └── Diagnostics/             # TradingMetrics, MarketRegimeDetector
│   ├── TradingBot.Infrastructure/   # Binance.Net, EF Core, Redis, Serilog
│   └── TradingBot.Frontend/         # Blazor WASM: Home, Strategies, StrategyDetail, Orders,
│                                    # Positions, Backtest, Optimizer, Metrics, Login
└── tests/                           # Core (80) + Application (290) + Integration (8)
```

---

## 📊 Flujo del Motor

```
Kline (vela cerrada) → ProcessKlineAsync → indicadores → señales
  → EvaluateAsync → ValidateOrderAsync → PlaceOrderAsync
  → EvaluateExitRulesAsync(evaluateIndicatorRules: true)  // reglas custom

Tick → NotifyMarketTickAsync → SignalR
  → position.UpdatePrice → EvaluateExitRulesAsync(evaluateIndicatorRules: false)
    → Solo SL/TP/trailing → PlaceOrderAsync  // cierra posición
```

---

## 🔌 API Endpoints

| Área | Endpoints principales |
|------|----------------------|
| **Estrategias** | `GET/POST/PUT/DELETE /api/strategies[/{id}]`, `activate`, `deactivate`, `duplicate`, `indicators`, `rules`, `optimization-profile`, `templates` |
| **Órdenes** | `GET /api/orders[/open]`, `DELETE /api/orders/{id}` |
| **Posiciones** | `GET /api/positions/open\|closed\|summary` |
| **Backtest** | `POST /api/backtest`, `optimize`, `walk-forward` |
| **Sistema** | `GET /api/system/status\|symbols\|balance\|exposure`, `POST pause\|resume` |
| **Health** | `GET /health[/ready\|/live]` |
| **SignalR** | `/hubs/trading`: `OnMarketTick`, `OnOrderExecuted`, `OnSignalGenerated`, `OnAlert`, `OnStrategyUpdated` |

---

## ⚙️ Configuración

```bash
BINANCE_API_KEY / BINANCE_API_SECRET    # Credenciales Binance
BINANCE_USE_TESTNET=true                # testnet.binance.vision
BINANCE_USE_DEMO=false                  # demo.binance.com
REDIS_CONNECTION=localhost:6379
TRADINGBOT_API_KEY=your_secret          # Header X-Api-Key (vacío = sin auth)
```

| Modo | Descripción |
|------|-------------|
| `Live` | Dinero real en Binance |
| `Testnet` | Entorno de pruebas Binance |
| `PaperTrading` | Simulación local sin exchange |

---

## 📐 Order State Machine

```
Pending → Submitted → Filled ✓
                    → PartiallyFilled → Filled ✓
                    → Rejected ✓
        → Cancelled ✓
```

---

## 🤖 AutoPilot v2 — Pool Dinámico de Símbolos

Sistema de selección dinámica de símbolos que coexiste con el modo manual y AutoPilot v1.
Observa 30-50 símbolos, calcula un **TradabilityScore en tiempo real** desde WebSocket,
y solo permite operar en los **Top 3-5** que superen un umbral mínimo.

> **Filosofía**: El bot no elige símbolos ganadores, elige **momentos operables** dentro de cada símbolo.

### Flujo del ciclo (cada 90 segundos)

```
MarketScannerWorker (REST cada 5min)
  → candidatos por liquidez/spread/volumen
  → SymbolPoolManager consume

SymbolPoolManager (BackgroundService, ciclo cada 90s)
  1. Actualizar universo (consumir scanner)
  2. Reconciliar runners (start/stop, respetar MaxConcurrentRunners)
  3. TradabilityScore (snapshot atómico + normalización 0-1 + regime stability)
  4. Histéresis ranking (contadores ciclos consecutivos)
  5. Top K = score > MinTradabilityScore + histéresis (vacío = no operar)
  6. SetAllowNewEntries (true Top K, false resto)
  7. EnteredTopKAt solo en transiciones (!wasActive && isActive)
  8. Cleanup zombies (fuera pool + sin posición + idle + score bajo)
  9. Publicar métricas de bloqueo vía ITradingNotifier
```

### TradabilityScore (normalizado 0-100)

| Factor | Normalización | Peso |
|--------|---------------|------|
| Claridad régimen | Trending/Ranging/Bearish=1.0, Unknown=0.1, Indefinite=0 | 25% |
| Fuerza ADX | 0→ADX≤15, 1→ADX≥30, lineal | 20% |
| Volumen relativo | 0→ratio≤0.5, 1→ratio≥1.5, lineal | 15% |
| ATR% saludable | Lineal por tramos: 0→<0.5%, 1→~2%, 0→>5% | 12.5% |
| BandWidth | 0→>0.08 o <0.01, 1→0.03-0.05 | 12.5% |
| Signal Proximity | Régimen-aware (Trending: RSI+MACD; Ranging: RSI+BB) | 15% |

**Fórmula**: `finalScore = rawScore × (0.7 + 0.3 × regimeStability)`

### Protecciones

| Mecanismo | Descripción |
|-----------|-------------|
| **Histéresis ranking** | 2 ciclos consecutivos para entrar/salir del Top K |
| **Cooldown entrada** | 120s mínimo en Top K antes de generar señales |
| **`EnteredTopKAt` en transición** | Solo se setea al entrar, no si ya estaba |
| **Top K vacío** | Si ningún símbolo supera umbral → no operar |
| **Posiciones al salir** | No se cierran; SL/TP sigue activo, solo `AllowNewEntries=false` |
| **Zombie cleanup** | Runners idle + score bajo + sin posición → se detienen |
| **Cap runners** | `MaxConcurrentRunners` (default 40) |
| **Thread safety** | `volatile AllowNewEntries`, lock por runner en snapshots/transiciones |

### Configuración (`appsettings.json` → sección `SymbolPool`)

```json
{
  "SymbolPool": {
    "Enabled": false,
    "ObservedPoolSize": 30,
    "ActiveTopK": 5,
    "MaxConcurrentRunners": 40,
    "EvaluationIntervalSeconds": 90,
    "BaseTemplateId": "trend-rider-alcista",
    "DefaultTimeframe": "OneHour",
    "DefaultTradingMode": "PaperTrading",
    "MinCyclesInTopK": 2,
    "MinCyclesOutOfTopK": 2,
    "MinTimeInTopKBeforeEntrySeconds": 120,
    "IdleTimeoutMinutes": 15,
    "ZombieScoreThreshold": 20,
    "MinTradabilityScore": 40
  }
}
```

### Métricas de diagnóstico

Cada ciclo publica vía SignalR:
```
"{Evaluated} evaluados, {BlockedByRegime} bloqueados régimen,
 {BlockedByScore} por score < {Min}, {Active} activos Top K, {Zombies} eliminados"
```

### Archivos principales

| Archivo | Rol |
|---------|-----|
| `ISymbolPool.cs` | Interfaz consulta estado pool |
| `SymbolPoolConfig.cs` | Configuración hot-reloadable |
| `TradabilityScorer.cs` | Score normalizado 0-100 |
| `SymbolPoolManager.cs` | BackgroundService orquestador |
| `StrategyEngine.cs` | Guard `AllowNewEntries` + métodos pool |
| `DefaultTradingStrategy.cs` | `GetSignalProximity()` + `GetRegimeStability()` |

---

## 🔧 Comandos

```powershell
dotnet build; dotnet test
dotnet ef migrations add <Nombre> --project src\TradingBot.Infrastructure --startup-project src\TradingBot.API
docker compose up -d
```

## 🚀 Roadmap

| Fase | Estado |
|------|--------|
| 1 — MVP: WebSocket, Dashboard, CRUD, Paper Trading | ✅ |
| 2 — Core Trading: Órdenes reales, Risk Manager, Backtesting | ✅ |
| 3 — Hardening: Auditorías CRIT/TRADE, correcciones pre-Testnet | ✅ |
| 4 — Producción: Validación Testnet, Reconciliación, Alertas | ⏳ |
| 5 — AutoPilot v2: Pool Dinámico, TradabilityScore, selección dinámica de símbolos | 🔜 |

---

> ⚠️ Software para fines educativos. El trading de criptomonedas conlleva riesgos significativos.
> Siempre prueba en Testnet/Paper Trading antes de usar dinero real.
