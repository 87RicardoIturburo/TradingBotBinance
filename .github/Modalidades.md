# TradingBot — Modalidades Propuestas para Automatización Inteligente

> **Problema central**: El usuario no sabe de trading → el bot debe decidir **qué estrategia**, **en qué symbol**, **por cuánto tiempo** y **con cuánto capital** operar.
>
> **Última actualización**: Junio 2025

---

## 📋 Resumen ejecutivo

| # | Módulo | Impacto para novato | Complejidad | Dependencias |
|---|--------|:-------------------:|:-----------:|:------------:|
| 1 | **Market Scanner** | ⭐⭐⭐⭐⭐ | 🟡 Media | Ninguna |
| 2 | **Auto-Backtest Ranker** (+ SymbolProfiler) | ⭐⭐⭐⭐⭐ | 🟡 Media | Ninguna |
| 3 | **Auto-Pilot / Strategy Rotator** | ⭐⭐⭐⭐ | 🟡 Media | Scanner (opcional) |
| 4 | **Risk Budget Guardian** | ⭐⭐⭐⭐⭐ | 🟢 Baja | Ninguna |
| 5 | **Setup Wizard** | ⭐⭐⭐⭐⭐ | 🟡 Media | 1 + 2 + 4 |
| 6 | **Trade Explainer** | ⭐⭐⭐⭐ | 🟢 Baja | Ninguna |

### Orden de implementación recomendado

> **4 → 2 → 1 → 6 → 3 → 5**
>
> Primero proteger capital (4), luego saber qué funciona (2), después dónde mirar (1),
> entender qué hace el bot (6), automatizar rotación (3), y finalmente el wizard que une todo (5).

### Flujo de dependencias

```
Market Scanner (1) ──→ Auto-Backtest Ranker (2) ──→ Auto-Pilot / Rotator (3)
       │                        │                            │
       ▼                        ▼                            ▼
Risk Budget Guardian (4) ◄──────┴────────────────► Setup Wizard (5)
       │
       ▼
Trade Explainer (6)
```

### ⚠️ Concepto clave: flujo multi-symbol

> **Hoy**: Cada `TradingStrategy` está atada a UN symbol fijo (ej. BTCUSDT). Si BNB está en alza,
> una estrategia creada para BTC **no opera BNB**. El symbol solo se puede cambiar cuando la
> estrategia está inactiva.
>
> **Con los módulos**: La automatización completa requiere que los módulos 1 + 2 trabajen juntos
> para **crear estrategias dinámicamente** en symbols que el Scanner detecte como oportunidades.

```
Flujo actual (manual):
  Tú eliges BTCUSDT → Tú eliges Trend Rider → Tú creas la estrategia → Tú activas

Flujo automatizado (módulos 1 + 2 + 3):
  Scanner detecta "BNBUSDT score 92 🟢"
    → Ranker prueba 7 templates CONTRA BNB (override del symbol default del template)
    → RiskConfig se ADAPTA al symbol (ver sección "Riesgos del override")
    → Ranker dice "Trend Rider +12% para BNB"
    → Sistema CREA "Trend Rider — BNBUSDT" automáticamente
    → Auto-Pilot monitorea y rota si el régimen cambia
    → Si BNB pierde score → desactiva y limpia
```

El **Ranker (módulo 2) no usa el symbol del template** — recibe el symbol del Scanner o del
usuario y ejecuta todos los templates contra ese symbol.

### ⚠️ Riesgos del override de symbol

El override a nivel técnico es seguro (`Symbol` es un value object puro, los indicadores son
agnósticos al symbol, los filtros de exchange se validan al ejecutar órdenes). Los riesgos reales:

| Riesgo | Severidad | Detalle |
|--------|:---------:|---------|
| **UI no permite override** | 🔴 | `ApplyTemplateAsync` hardcodea `tpl.Symbol`. El Ranker lo resolvería programáticamente. Para uso manual, la UI de templates necesita un selector de symbol. |
| **RiskConfig no adaptado** | 🟡 | Parámetros como `VolumeSMA minRatio`, umbrales de volatilidad (`HighVolatilityBandWidthPercent`, `HighVolatilityAtrPercent`) y `MaxSpreadPercent` están calibrados para BTC/ETH. Aplicarlos a altcoins de baja liquidez produce falsos positivos/negativos. **Solución: `SymbolProfiler`** — un servicio que analiza datos históricos del symbol y ajusta estos parámetros automáticamente (ver módulo 2). |
| **Nombre confuso** | 🟢 | "Trend Rider" sin indicar el symbol real. Solución: concatenar `"{Template} — {Symbol}"`. |

---

## 1. 🧭 Market Scanner (Escáner de Mercado)

> *"¿Qué symbols vale la pena operar AHORA?"*

### Problema que resuelve

Hay +600 pares USDT en Binance. El usuario no sabe cuál elegir. El scanner analiza el mercado
en tiempo real y presenta un ranking con los mejores candidatos.

### Qué haría

- Cada X minutos, escanear los top 50-100 pares por volumen 24h
- Para cada par, calcular un **"Tradability Score"** basado en:
  - **Volumen 24h** → liquidez → spreads bajos
  - **Volatilidad ATR%** → oportunidad (pero no excesiva)
  - **Régimen actual** → Trending > Ranging > HighVolatility
  - **Fuerza de tendencia** → ADX
  - **Spread actual** → costo oculto de operar
- Mostrar un **ranking** en el frontend con semáforo:
  - 🟢 Operable (score alto, buena liquidez, régimen favorable)
  - 🟡 Cauto (score medio, alguna condición desfavorable)
  - 🔴 Evitar (baja liquidez, alta volatilidad, spread amplio)

### Arquitectura propuesta

```
IMarketScanner → MarketScannerService (BackgroundService)
  → Consume: IMarketDataService.GetTradingSymbolsAsync() + Get24hTickersAsync()
  → Calcula: TradabilityScore por symbol
  → Publica: SymbolScoreUpdatedEvent via SignalR al frontend
  → Persiste: SymbolScore en Redis (TTL 5min)
```

**Capa Core**:
- `IMarketScanner` (interfaz)
- `SymbolScore` (entidad/VO con Symbol, Score, Regime, Volume24h, Spread, etc.)
- `MarketScanCompleted` (evento de dominio)

**Capa Application**:
- `MarketScannerService : BackgroundService`
- `GetTopSymbolsQuery / GetTopSymbolsQueryHandler`

**Capa Infrastructure**:
- Llamadas a Binance REST: tickers 24h + exchange info
- Cache en Redis con TTL

**Frontend**:
- Página `Scanner.razor` con tabla ordenable
- Componente `SymbolCard.razor` con semáforo visual
- Actualización en tiempo real via SignalR

### Métricas del Tradability Score (pesos sugeridos)

| Factor | Peso | Criterio 🟢 | Criterio 🟡 | Criterio 🔴 |
|--------|:----:|:----------:|:----------:|:----------:|
| Volumen 24h | 30% | > $50M | $10M–$50M | < $10M |
| Spread | 20% | < 0.05% | 0.05%–0.15% | > 0.15% |
| ATR% (volatilidad) | 20% | 1%–4% | 0.5%–1% o 4%–6% | < 0.5% o > 6% |
| Régimen | 20% | Trending | Ranging | HighVolatility |
| ADX (fuerza) | 10% | > 25 | 15–25 | < 15 |

### Complejidad: 🟡 Media

Ya existe `GetTradingSymbolsAsync` y `MarketRegimeDetector`. Falta obtener tickers 24h en batch
y calcular el score compuesto.

### Estado: [x] Implementado

**Archivos creados:**
- `Core/Interfaces/Services/IMarketScanner.cs` — Interfaz + record `SymbolScore`
- `Core/Interfaces/Services/IMarketDataService.cs` — Añadido `Get24hTickersAsync` + record `Ticker24h`
- `Application/Scanner/MarketScannerConfig.cs` — Config hot-reloadable (pesos, intervalo, quote asset)
- `Application/Scanner/MarketScannerService.cs` — Implementación: score compuesto con 5 factores ponderados
- `Application/Scanner/MarketScannerWorker.cs` — BackgroundService que escanea cada N minutos
- `Application/Scanner/GetTopSymbolsQuery.cs` — CQRS query + handler
- `API/Controllers/ScannerController.cs` — Endpoint `GET /api/scanner`

**Archivos modificados:**
- `Infrastructure/Binance/MarketDataService.cs` — Implementación de `Get24hTickersAsync`
- `API/Hubs/TradingHub.cs` — Evento `OnScannerUpdate`
- `API/Services/SignalRTradingNotifier.cs` — `NotifyScannerUpdateAsync`
- `Core/Interfaces/Services/ITradingNotifier.cs` — Añadido `NotifyScannerUpdateAsync`
- `Application/ApplicationServiceExtensions.cs` — DI: IMarketScanner + config + worker
- `API/appsettings.json` — Sección `MarketScanner`
- `API/Dtos/Dtos.cs` — `SymbolScoreDto`

**Tests:** 344 total, 0 fallos (tests existentes no afectados)

---

## 2. 📊 Auto-Backtest Ranker

> *"Para BTCUSDT, ¿cuál de mis templates habría ganado más el último mes?"*

### Problema que resuelve

Hay 7 templates de estrategia. El usuario no sabe cuál usar para un symbol dado.
El ranker ejecuta backtest de **todas** las plantillas sobre datos recientes y presenta un ranking.

### Qué haría

- El usuario selecciona un symbol (o el Scanner le sugiere uno)
- El sistema **overridea el symbol default de cada template** con el symbol seleccionado
  (ej. Trend Rider dice "BTCUSDT" por default, pero se testea contra BNBUSDT si ese fue el input)
- **Antes de correr backtest, el `SymbolProfiler` adapta el RiskConfig** al symbol real
  (ver sub-componente abajo)
- Corre backtest de las 7 plantillas sobre los últimos 30 días
- Muestra ranking ordenado por Sharpe Ratio:
  - *"Trend Rider: +8.2% (Sharpe 1.4) | Range Scalper: +3.1% (Sharpe 0.9) | MACD: -1.4% (Sharpe -0.3)..."*
- Botón: **"Activar la mejor"** → crea la estrategia con symbol + RiskConfig ya adaptados

### Sub-componente: `SymbolProfiler` (Adaptación de RiskConfig)

Los templates tienen parámetros calibrados para BTC/ETH. Al aplicarlos a otro symbol, algunos
parámetros producen falsos positivos/negativos. El `SymbolProfiler` analiza datos históricos
del symbol objetivo y ajusta los parámetros que son symbol-dependientes.

#### Clasificación de parámetros de RiskConfig

**✅ Universales (no necesitan adaptación)** — funcionan con cualquier symbol:

| Parámetro | Por qué es universal |
|-----------|---------------------|
| `MaxOrderAmountUsdt` / `MaxDailyLossUsdt` | Montos en USDT, presupuesto del usuario |
| `StopLossPercent` / `TakeProfitPercent` | Porcentuales, escalan con el precio |
| `MaxOpenPositions` | Preferencia del usuario |
| `RiskPercentPerTrade` | Tolerancia al riesgo del usuario |
| `AtrMultiplier` | ATR ya se adapta solo a la volatilidad del symbol |
| `UseAtrSizing` / `UseTrailingStop` / `ExitOnRegimeChange` | Flags booleanos |
| `TrailingStopPercent` | Porcentual |
| `ConfirmationEmaPeriod` / `SignalCooldownPercent` | Períodos/porcentajes |
| `AdxTrendingThreshold` / `AdxRangingThreshold` | ADX es normalizado 0–100 |
| `MinConfirmationPercent` | Porcentaje de consenso |
| `TakeProfit1/2Percent` / `ClosePercent` | Porcentuales |
| `TakeProfit1/2AtrMultiplier` | ATR ya se adapta |
| `MaxPositionDurationCandles` | Conteo de velas |

**⚠️ Symbol-dependientes (el `SymbolProfiler` los ajusta)**:

| Parámetro | Valor template (BTC) | Problema en altcoins | Cómo adaptar |
|-----------|:--------------------:|----------------------|-------------|
| `HighVolatilityAtrPercent` | 0.03 (3%) | Altcoins tienen ATR% normal de 5-8%, se suprimiría TODO | Mediana de ATR% histórico × 2 |
| `HighVolatilityBandWidthPercent` | 0.08 (8%) | Memecoins tienen BW normal > 8% | Mediana de BandWidth histórico × 2 |
| `MaxSpreadPercent` | 0.5% | BTC tiene 0.01%, altcoins ilíquidas 0.3-1%+ | Spread actual del ticker × 3 |
| `VolumeSMA minRatio`* | 1.3× | El volumen de BTC es ultra-consistente, altcoins son erráticas | Basado en coeficiente de variación del volumen |

> *`VolumeSMA minRatio` está en `IndicatorConfig`, no en `RiskConfig`, pero necesita el mismo ajuste.

#### Lógica del SymbolProfiler

```
ISymbolProfiler → SymbolProfilerService

Entrada: symbol + klines históricas (las mismas que descargó el Ranker)
Salida:  SymbolProfile (record con ajustes calculados)

1. Calcular ATR(14) sobre las klines → obtener mediana de ATR/Close
   → HighVolatilityAtrPercent = mediana × 2.0

2. Calcular BB(20,2) BandWidth sobre las klines → obtener mediana
   → HighVolatilityBandWidthPercent = mediana × 2.0

3. Obtener spread actual via IMarketDataService.GetLastBidAsk()
   → MaxSpreadPercent = max(spread% × 3, 0.1%)

4. Calcular coeficiente de variación del volumen (σ/μ)
   → Si CV < 0.5 (consistente, como BTC): minRatio = 1.3
   → Si CV 0.5-1.0 (moderado): minRatio = 1.5
   → Si CV > 1.0 (errático, memecoins): minRatio = 2.0
```

**El profiler reutiliza las klines que el Ranker ya descargó** — cero llamadas extra a Binance.

#### Arquitectura

```
RunTemplateRankingCommand : IRequest<TemplateRankingResult>
  → Descarga klines UNA vez para el symbol
  → SymbolProfiler.AnalyzeAsync(klines, symbol) → SymbolProfile
  → Para cada StrategyTemplate en StrategyTemplates.All:
      → Crea TradingStrategy temporal con symbol overrideado
      → Aplica SymbolProfile al RiskConfig + IndicatorConfig
      → BacktestEngine.RunAsync(strategy, klines)
  → Parallel.ForEachAsync (patrón ya existente en OptimizationEngine)
  → Retorna ranking ordenado por métrica seleccionada
```

**Capa Core**:
- `ISymbolProfiler` (interfaz)
- `SymbolProfile` (record: AdjustedAtrPercent, AdjustedBandWidthPercent, AdjustedMaxSpread,
   AdjustedVolumeMinRatio, MedianAtrPercent, MedianBandWidth, AvgVolume24h, CurrentSpread)

**Capa Application**:
- `SymbolProfilerService : ISymbolProfiler`
- `RunTemplateRankingCommand` / `RunTemplateRankingCommandHandler`
- `TemplateRankingResult` (record con lista de `TemplateRankEntry`)
- `TemplateRankEntry` (TemplateId, Name, TotalPnL, SharpeRatio, WinRate, MaxDrawdown,
   TotalTrades, AppliedProfile)

**Capa API**:
- `POST /api/backtest/rank-templates` → `{ symbol, fromDays, interval }`
- Reutilizar `BacktestController`

**Frontend**:
- Sección en `Strategies.razor` o nueva página `TemplateRanker.razor`
- Tabla con barras de progreso visuales por métrica
- Badge que indica: "RiskConfig adaptado para BNBUSDT (ATR% normal: 4.2%)"
- Botón "Crear estrategia con este template"

### Complejidad: 🟢 Baja → 🟡 Media (por el SymbolProfiler)

BacktestEngine, StrategyTemplates.All, GetKlinesAsync ya existen.
El SymbolProfiler es cálculo puro sobre klines (sin I/O extra), complejidad baja.

### Estado: [x] Implementado

**Archivos creados:**
- `Application/Backtesting/StrategyTemplateDtos.cs` — Records de template en capa Application
- `Application/Backtesting/StrategyTemplateStore.cs` — Fuente única de verdad para templates (7 templates)
- `Application/Backtesting/SymbolProfile.cs` — Record con perfil calculado del symbol
- `Application/Backtesting/SymbolProfiler.cs` — Calcula ATR%, BandWidth, VolumeCV, ajusta umbrales
- `Application/Backtesting/RunTemplateRankingCommand.cs` — MediatR command + handler
- `API/Controllers/BacktestController.cs` — Endpoint `POST /api/backtest/rank-templates`
- `API/Dtos/Dtos.cs` — DTOs: `RankTemplatesRequest`, `TemplateRankingResultDto`, `SymbolProfileDto`

**Archivos modificados:**
- `API/Dtos/StrategyTemplates.cs` — Ahora delega a `StrategyTemplateStore` de Application

**Tests:** 9 tests en `SymbolProfilerTests.cs` (344 total, 0 fallos)

---

## 3. 🤖 Auto-Pilot / Strategy Rotator (Meta-Estrategia)

> *"Que el bot decida qué estrategia usar según el mercado actual"*

### Problema que resuelve

Las 3 estrategias por tipo de mercado (Trend Rider, Bottom Catcher, Range Scalper) ya existen,
pero el usuario tiene que activar/desactivar manualmente cuando el mercado cambia. Este módulo
automatiza la rotación.

> **Nota**: Cada instancia del rotator opera sobre **un symbol específico**. Si el Scanner
> (módulo 1) detecta que BNB y ETH son buenos candidatos, se crea un rotator por symbol.
> Cada uno mantiene 3 estrategias pre-creadas (alcista/bajista/lateral) para SU symbol.
> Si un symbol pierde score en el Scanner → el rotator pausa y limpia sus estrategias.

### Qué haría

- Monitorear el `MarketRegime` del symbol continuamente
- Cuando cambia el régimen:

| Régimen detectado | Acción |
|-------------------|--------|
| `Trending + Bullish` (+DI > -DI) | Activar Trend Rider, pausar Range Scalper |
| `Ranging` (ADX < 20) | Activar Range Scalper, pausar Trend Rider |
| `Trending + Bearish` (-DI > +DI) | Activar Bottom Catcher (modo defensivo) |
| `HighVolatility` | **Pausar TODO** (protección de capital) |

- **Transiciones suaves**: cerrar posiciones de la estrategia saliente antes de activar la entrante
- **Histéresis**: no cambiar si el régimen oscila (ya implementada en EST-19)
- **Cooldown de rotación**: mínimo 2 horas entre cambios para evitar whipsaw
- **Multi-symbol**: una instancia de rotación por cada symbol activo (limitado por Risk Budget)

### Arquitectura propuesta

```
IStrategyRotator → StrategyRotatorService : BackgroundService
  → Consume: MarketRegimeChangedEvent (o polling cada N klines)
  → Evalúa: régimen actual vs estrategia activa
  → Si cambio necesario:
      → Cierra posiciones abiertas de estrategia saliente
      → Desactiva estrategia saliente via IStrategyConfigService
      → Activa estrategia entrante con config predefinida
  → Publica: StrategyRotatedEvent via SignalR
```

**Configuración (hot-reloadable JSON)**:
```json
{
  "AutoPilot": {
    "Enabled": true,
    "Symbol": "BTCUSDT",
    "RotationCooldownMinutes": 120,
    "TrendingStrategy": "trend-rider-alcista",
    "RangingStrategy": "range-scalper-lateral",
    "BearishStrategy": "defensive-bottom-catcher-bajista",
    "HighVolatilityAction": "PauseAll",
    "ClosePositionsOnRotation": true
  }
}
```

### Complejidad: 🟡 Media

La lógica de régimen ya existe. El reto principal es la **transición limpia**: cerrar posiciones
de una estrategia y abrir otra sin gaps ni conflictos en el StrategyEngine.

### Estado: [x] Implementado

**Archivos creados:**
- `Core/Interfaces/Services/IStrategyRotator.cs` — Interfaz + record `RotationResult`
- `Application/AutoPilot/AutoPilotConfig.cs` — Config hot-reloadable (cooldown, templates, HighVolatility action)
- `Application/AutoPilot/StrategyRotatorService.cs` — Lógica de rotación: selección de template por régimen, cooldown, creación/activación automática
- `Application/AutoPilot/AutoPilotWorker.cs` — BackgroundService que evalúa regímenes cada 5 min
- `API/Controllers/AutoPilotController.cs` — Endpoints `POST /api/autopilot/evaluate` y `GET /api/autopilot/status`

**Archivos modificados:**
- `Application/ApplicationServiceExtensions.cs` — DI: IStrategyRotator + config + worker
- `API/appsettings.json` — Sección `AutoPilot` con templates y cooldown

**Tests:** 344 total, 0 fallos

> *"Tengo $500. No quiero perder más de $50 en total. Nunca."*

### Problema que resuelve

Existe `MaxDailyLossUsdt` por estrategia y circuit breaker, pero no hay un **presupuesto global**
que diga "de mi capital total, nunca arriesgar más del X%". Un novato necesita una red de
seguridad absoluta.

### Qué haría

- El usuario define:
  - **Capital total**: $500
  - **Pérdida máxima aceptable**: 10% ($50)
  - **Perfil**: Conservador / Moderado / Agresivo
- El sistema calcula dinámicamente:
  - Cuánto asignar por estrategia (reparto fijo o proporcional al Sharpe)
  - Reducción automática de `MaxOrderAmountUsdt` conforme se acumula pérdida
  - **Kill switch global** cuando pérdida acumulada ≥ umbral → para TODO
- Escala inversa: si vas perdiendo, reduce exposición; si vas ganando, permite aumentar gradualmente

### Tabla de ajuste dinámico (ejemplo con $500 capital, 10% max loss)

| Pérdida acumulada | % del max loss | Acción |
|:-----------------:|:--------------:|--------|
| $0 – $15 | 0% – 30% | Operación normal |
| $15 – $30 | 30% – 60% | Reducir `MaxOrderAmountUsdt` al 70% |
| $30 – $40 | 60% – 80% | Reducir al 40%, `MaxOpenPositions = 1` |
| $40 – $50 | 80% – 100% | Solo cerrar posiciones abiertas, no abrir nuevas |
| ≥ $50 | 100% | **Kill switch**: pausar todas las estrategias |

### Arquitectura propuesta

```
IRiskBudget → RiskBudgetService
  → Lee: capital total + pérdida acumulada (de OrderHistory en BD)
  → Calcula: nivel de riesgo actual (Normal / Reduced / Critical / Exhausted)
  → Ajusta: RiskConfig.MaxOrderAmountUsdt en tiempo real
  → Evento: BudgetExhaustedEvent → pausa todas las estrategias
  → Persiste: RiskBudgetSnapshot en BD (para auditoría)
```

**Capa Core**:
- `IRiskBudget` (interfaz)
- `RiskBudgetConfig` (record: TotalCapital, MaxLossPercent, Profile)
- `RiskLevel` (enum: Normal, Reduced, Critical, Exhausted)
- `BudgetExhaustedEvent` (evento de dominio)

**Capa Application**:
- `RiskBudgetService` (singleton, consultado por `RiskManager` antes de cada orden)
- `GetRiskBudgetStatusQuery` / handler

**Integración con RiskManager existente**:
- `RiskManager.ValidateAsync` ya valida antes de cada orden
- Agregar check: `if (riskBudget.CurrentLevel >= RiskLevel.Exhausted) → rechazar`

### Complejidad: 🟢 Baja–Media

Ya existen circuit breaker y drawdown. Esto es una capa superior que acumula pérdidas globales
y ajusta parámetros. La lectura de P&L histórico ya está en los repositorios.

### Estado: [x] Implementado

**Archivos creados:**
- `Core/Enums/RiskLevel.cs` — Enum: Normal, Reduced, Critical, CloseOnly, Exhausted
- `Core/Events/BudgetExhaustedEvent.cs` — Evento de dominio para kill switch
- `Core/Interfaces/Services/IRiskBudget.cs` — Interfaz del guardián
- `Application/RiskManagement/RiskBudgetConfig.cs` — Config: TotalCapital, MaxLossPercent, umbrales
- `Application/RiskManagement/RiskBudgetService.cs` — Implementación con protección progresiva

**Archivos modificados:**
- `Application/RiskManagement/RiskManager.cs` — Integración: consulta IRiskBudget antes de cada orden
- `Application/ApplicationServiceExtensions.cs` — DI: IRiskBudget + config InvariantCulture
- `API/appsettings.json` — Sección `RiskBudget` con defaults ($500, 10%)

**Tests:** 10 tests en `RiskBudgetServiceTests.cs`, 24 en `RiskManagerTests.cs` actualizados (344 total, 0 fallos)

---

## 5. 🧙 Setup Wizard (Asistente Guiado)

> *"Responde 4 preguntas y te configuro todo"*

### Problema que resuelve

La UI actual requiere entender indicadores, timeframes, risk config. Para alguien que no sabe
de trading, es abrumador. El wizard abstrae toda la complejidad.

### Flujo del wizard (4 pasos)

| Paso | Pregunta | Opciones |
|:----:|----------|----------|
| 1 | ¿Cuánto capital quieres usar? | $100 / $500 / $1,000 / Custom |
| 2 | ¿Cuánto estás dispuesto a perder? | Conservador (5%) / Moderado (10%) / Agresivo (20%) |
| 3 | ¿Qué tan seguido quieres revisar? | Diario / Semanal / "No quiero pensar en esto" |
| 4 | ¿Empezar con dinero real? | Paper Trading primero ✅ / Demo / Producción |

### Qué haría con las respuestas

1. Configura **Risk Budget Guardian** (módulo 4) con capital y tolerancia
2. Llama al **Market Scanner** (módulo 1) → top 3 symbols operables
3. Llama al **Auto-Backtest Ranker** (módulo 2) → mejor template por symbol
4. Crea las estrategias automáticamente en el modo seleccionado (Paper/Demo/Prod)
5. Si eligió "No quiero pensar" → activa **Auto-Pilot** (módulo 3)
6. Muestra resumen:
   > *"Listo. Creé Trend Rider en BTCUSDT y Range Scalper en ETHUSDT.*
   > *Primero 2 semanas en Paper Trading. Tu presupuesto de riesgo: máx $50 de pérdida."*

### Arquitectura propuesta

```
Frontend: SetupWizard.razor (stepper de 4 pasos)
  → Paso 1-2: Configura RiskBudgetConfig
  → Paso 3: Define frecuencia de monitoreo + auto-pilot
  → Paso 4: Define TradingMode
  → Submit: RunSetupWizardCommand : IRequest<SetupWizardResult>
      → Orquesta módulos 1, 2, 3, 4
      → Retorna resumen de lo creado
```

**Frontend**:
- `SetupWizard.razor` — stepper visual con iconos y descripciones amigables
- Cada paso es un componente independiente (`WizardStep1Capital.razor`, etc.)
- Resultado final con tarjetas visuales de cada estrategia creada

### Complejidad: 🟡 Media

Es principalmente frontend (Blazor wizard) + un command que orquesta los módulos anteriores.
**Requiere que los módulos 1, 2 y 4 estén implementados.**

### Estado: [x] Implementado

**Archivos creados:**
- `Application/Wizard/RunSetupWizardCommand.cs` — Command + Handler: orquesta Scanner → Ranker → crea estrategias
- `API/Controllers/WizardController.cs` — Endpoint `POST /api/wizard`

**Flujo implementado:**
1. Configura Risk Budget según perfil (Conservador 5% / Moderado 10% / Agresivo 20%)
2. Escanea mercado → top 3 symbols operables
3. Ejecuta template ranking por symbol → selecciona mejor template
4. Crea y activa estrategias automáticamente con capital distribuido
5. Si `MonitoringFrequency = NoQuieroPensar` → habilita AutoPilot

**Tests:** 344 total, 0 fallos

---

## 6. 📈 Trade Explainer (Explicador de Trades)

> *"¿Por qué compró? ¿Fue buena decisión?"*

### Problema que resuelve

El bot opera pero el usuario no sabe si lo está haciendo bien o mal, ni por qué tomó cada
decisión. La transparencia genera confianza y aprendizaje.

### Qué haría

- Después de cada trade, generar un **resumen legible**:
  > *"Compré BTCUSDT a $67,200 porque: RSI estaba en 38 (sobreventa), EMA9 cruzó EMA21*
  > *(Golden Cross), volumen 1.8× del promedio, mercado en tendencia alcista (ADX=32)."*
  >
  > *"Vendí con +2.3% ganancia. TP1 alcanzado en 4 velas."*

- **Dashboard simplificado** con métricas semanales:
  - 🟢 "Esta semana: 3 trades, +$12.50 (+2.5%)"
  - 🔴 "Esta semana: 2 trades, -$5.30 (-1.1%)"

- **Historial de decisiones** con cada trade explicado:
  - Señal generadora (qué indicador la disparó)
  - Confirmaciones que pasó (cuántas y cuáles)
  - Filtros que superó (HTF, BTC correlation, cooldown)
  - Resultado final (P&L, duración, tipo de salida)

### Arquitectura propuesta

```
TradeExplanation (value object)
  → SignalGenerator: "EMA crossover (EMA9 > EMA21)"
  → Confirmations: ["MACD histograma > 0", "Volumen 1.8× promedio", "RSI = 38"]
  → Filters: ["HTF EMA confirmada", "BTC correlación alineada"]
  → MarketRegime: "Trending (ADX = 32)"
  → RiskCheck: "Position size: $150 (1% risk, ATR×2 SL)"

ITradeExplainer → TradeExplainerService
  → Consume: OrderPlacedEvent, PositionClosedEvent
  → Genera: TradeExplanation
  → Persiste: en campo JSON de la orden/posición
  → Publica: via SignalR al frontend
```

**Datos ya disponibles en el flujo actual**:
- `CountConfirmations` ya sabe qué indicadores confirmaron
- `DetermineSignalCandidate` sabe qué generador disparó la señal
- `MarketRegimeDetector.Detect` da el régimen
- `RiskManager.ValidateAsync` da los checks de riesgo
- Solo falta **serializar** esta información en un formato legible

**Frontend**:
- Componente `TradeCard.razor` con explicación expandible
- Sección en `Home.razor` con resumen semanal
- Página `TradeHistory.razor` con historial completo + filtros

### Complejidad: 🟢 Baja

Los datos ya existen en el flujo de señales. Solo falta capturarlos en un objeto estructurado
y mostrarlos en el frontend. No requiere lógica nueva, solo plumbing.

### Estado: [x] Implementado

**Archivos creados:**
- `Core/ValueObjects/TradeExplanation.cs` — Record con señal, confirmaciones, filtros, régimen, riesgo
- `Application/Explainer/TradeExplainerService.cs` — Builder de explicaciones (entrada + salida)
- `Application/Explainer/GetTradeHistoryQuery.cs` — CQRS query para historial con explicaciones

**Archivos modificados:**
- `Core/Entities/Order.cs` — Propiedad `Explanation` + método `SetExplanation`
- `Infrastructure/Persistence/Configurations/OrderConfiguration.cs` — `OwnsOne` JSON para Explanation
- `Application/Strategies/StrategyEngine.cs` — Captura explicación al colocar órdenes de entrada
- `API/Controllers/OrdersController.cs` — Endpoint `GET /api/orders/history`
- `API/Dtos/Dtos.cs` — `TradeExplanationDto`, `OrderWithExplanationDto`

**Migración:** `AddOrderExplanationJson`

**Tests:** 344 total, 0 fallos

---

## 🗓️ Roadmap sugerido

### Fase 1 — Protección y datos (semanas 1-2)
- [x] **Módulo 4**: Risk Budget Guardian ✅
- [x] **Módulo 2**: Auto-Backtest Ranker + SymbolProfiler ✅

### Fase 2 — Visibilidad e inteligencia (semanas 3-4)
- [x] **Módulo 1**: Market Scanner ✅
- [x] **Módulo 6**: Trade Explainer ✅

### Fase 3 — Automatización completa (semanas 5-6)
- [x] **Módulo 3**: Auto-Pilot / Strategy Rotator ✅
- [x] **Módulo 5**: Setup Wizard ✅

### Resultado final

Un usuario novato podría:
1. Abrir el bot → Setup Wizard le configura todo en 2 minutos
2. El Scanner encuentra los mejores symbols
3. El Ranker elige la mejor estrategia para cada symbol
4. El Auto-Pilot rota estrategias cuando el mercado cambia
5. El Risk Budget protege su capital con kill switch automático
6. El Explainer le dice qué pasó y por qué en lenguaje simple

**De "no sé nada de trading" → "el bot opera solo y yo solo reviso resultados".**
