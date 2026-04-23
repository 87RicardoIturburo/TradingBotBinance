# TradingBot — Plan de Trabajo & Análisis de Estrategias

> **Contexto para Copilot.** Leer junto con `.github/copilot-instructions.md` y `.github/PROJECT.md`.

---

## 📊 Estado actual

| Métrica | Valor |
|---------|-------|
| Build | ✅ 8 proyectos compilan |
| Tests | ✅ 344 passing |
| TFM | .NET 10 / C# 14 |
| Migraciones EF Core | 14 aplicadas |
| Etapas 1–7 | ✅ Completadas |
| Auditorías | ✅ Todas corregidas |
| Análisis rentabilidad | ✅ Julio 2025 — 6 hallazgos + backtest audit (5 hallazgos) + automatización (4 hallazgos) |

---

## 🔬 Análisis de Rentabilidad — Julio 2025

> **Problema**: Con $150-200 USD las ganancias/pérdidas rondan -$4.0 a +$0.9 por semana.
> **Causa raíz**: Arquitectura ultra-defensiva + templates sin parámetros avanzados + cierre proactivo prematuro.

### Hallazgos y correcciones aplicadas

| # | Hallazgo | Impacto | Estado |
|---|----------|---------|--------|
| R-1 | **EST-18 cierra posiciones con ganancia mínima** (+0.01%) que después de fees es pérdida neta | 🔴 ALTO | ✅ Corregido: umbral mínimo 0.5% o 25% del SL |
| R-2 | **Templates NO propagaban parámetros avanzados** (trailing stop, TP escalonado, MinConfirmation, cooldown) — siempre usaban defaults deshabilitados | 🔴 ALTO | ✅ Corregido: `StrategyTemplateRiskConfigDto` extendido con 12 parámetros nuevos |
| R-3 | **MinConfirmationPercent=50% por default** bloquea entradas válidas con 7 indicadores | 🟡 MEDIO | ✅ Corregido: templates actualizados con 30-40% según tipo |
| R-4 | **SignalCooldownPercent=50% por default** reduce frecuencia de señales | 🟡 MEDIO | ✅ Corregido: templates con 10-30% según tipo |
| R-5 | **Falta templates para capital bajo** ($100-300) con menos filtros y más operaciones | 🟡 MEDIO | ✅ 3 nuevos templates: Momentum Swing, Aggressive Trend, Quick Scalper 15m |
| R-6 | **Templates existentes sin trailing stop ni TP escalonado** — defaults los dejaban deshabilitados | 🟡 MEDIO | ✅ Trend Rider, Bottom Catcher, Range Scalper actualizados |

### Archivos modificados

| Archivo | Cambios |
|---------|---------|
| `StrategyEngine.cs` | R-1: Umbral mínimo de profit para cierre proactivo EST-18 |
| `StrategyTemplateDtos.cs` | R-2: 12 parámetros avanzados en `StrategyTemplateRiskConfigDto` |
| `StrategyTemplateStore.cs` | R-5/R-6: 3 nuevos templates + 3 existentes actualizados (10 total) |
| `RunTemplateRankingCommand.cs` | R-2: Propaga parámetros avanzados en `BuildAdaptedRiskConfig` |
| `RunSetupWizardCommand.cs` | R-2: Propaga parámetros avanzados al crear estrategia |
| `StrategyRotatorService.cs` | R-2: Propaga parámetros avanzados en AutoPilot |
| `StrategyTemplates.cs` (API) | R-2: DTO y conversión para frontend |

### Recomendaciones de configuración para $150-200 USD

```
Estrategia recomendada: "⚡ Momentum Swing Trader (Capital Bajo)"
- MaxOrder: $150 USDT
- Risk/trade: 2% ($3-4 por trade)
- SL: 2.5% — TP: 5% con TP escalonado (1.5% → 3%)
- MinConfirmation: 30% (más señales)
- Cooldown: 20% (mayor frecuencia)
- Trailing stop: 1.5% (protege ganancias)
- Trades esperados: 4-8/semana
- Profit semanal estimado: $3-15 (2-10% del capital)
```

---

## 📋 Pendientes — Operacional (antes de producción)

- [ ] IMP-2 (Alertas externas Telegram/webhook)
- [ ] Testnet operando estable ≥ 2 semanas sin errores críticos
- [ ] Sharpe Ratio walk-forward > 1.0 en al menos 2 estrategias
- [ ] Max drawdown en Testnet < 15%
- [ ] Kill switch global probado (manual + automático vía drawdown)
- [ ] Backup de BD verificado
- [ ] API Key de autenticación configurada (`TRADINGBOT_API_KEY`)
- [ ] `appsettings.json` sin secrets hardcoded

## 📋 Pendientes — Post-estabilización

- [ ] **Módulo 7: Auto-Optimizer** — Optimización automática en background (ver `.github/Modalidades.md § 7`)
  - Worker que detecta estrategias recién creadas y optimiza SL/TP/períodos
  - Walk-forward obligatorio para evitar overfitting
  - ~60-100 combinaciones por template, ~30s por estrategia
  - Requiere datos reales de Paper Trading para validar

---

## 🚀 Activación por fases

1. **Alpha**: 1 estrategia, 1 símbolo, máx $50 USDT, 2 semanas
2. **Beta**: 2-3 estrategias, máx $200 USDT, 1 mes
3. **Producción**: escala gradual según performance real

---

## 🔧 Convenciones técnicas

- **Enums en JSON**: siempre como strings (`JsonStringEnumConverter`)
- **Locale**: `CultureInfo.InvariantCulture` para parseo de decimales
- **Tests de integración**: `[Collection(nameof(SharedFactoryCollection))]`
- **EF Core owned entities**: `FixNewOwnedEntitiesTrackedAsModified()` en `SaveChangesAsync`
- **`decimal` en `[InlineData]`**: no permitido — usar `TheoryData<>` con `[MemberData]`
- **`InternalsVisibleTo`**: configurado en `TradingBot.Application.csproj` para tests + NSubstitute
- **Indicadores**: solo se alimentan con klines cerradas (`ProcessKlineAsync`), nunca con ticks
- **Reglas de salida**: tick loop → solo SL/TP/trailing; kline loop → reglas custom con indicadores

---

# 📈 ANÁLISIS EXPERTO DE ESTRATEGIAS DE TRADING

> **Última revisión**: Análisis completo del código fuente — `DefaultTradingStrategy`, `RuleEngine`,
> `RiskManager`, `PositionSizer`, `MarketRegimeDetector`, todos los indicadores y `StrategyEngine`.

---

## 1. Auditoría del sistema actual — `DefaultTradingStrategy`

### 1.1 Flujo de señales

```
Kline cerrada
  → Actualizar indicadores (OHLC/Volume/Close según tipo)
  → MarketRegimeDetector (ADX + BB BandWidth + ATR%)
  → [HighVolatility? → suprimir]
  → DetermineSignalCandidate (generadores priorizados por régimen)
  → Filtro direccional ADX (+DI/-DI vs lado de la señal)
  → Cooldown temporal
  → CountConfirmations (indicadores no-generadores votan)
  → Filtro HTF (EMA de confirmación en timeframe superior)
  → Anti-duplicado Spot (no abrir si ya hay Long abierta)
  → RuleEngine → RiskManager → PositionSizer → Orden
```

**Generadores de señal por régimen**:
- **Trending** → MACD → EMA → SMA → RSI
- **Ranging** → RSI → BB → MACD → EMA → SMA
- **Unknown** → RSI → MACD → BB → EMA → SMA

**Confirmadores**: MACD (histograma), BB (bandas), EMA, SMA, RSI (zona), LinearRegression (slope + R²), Fibonacci (niveles contextuales), VolumeSMA (ratio ≥ 1.5)

### 1.2 Fortalezas confirmadas

| Aspecto | Detalle | Impacto |
|---------|---------|---------|
| **Detección de régimen** | ADX + BB BandWidth + ATR relativo clasifica Trending/Ranging/HighVolatility | Evita aplicar estrategias equivocadas al mercado actual |
| **Supresión alta volatilidad** | `MarketRegime.HighVolatility` → cero señales | Protege contra whipsaws y flash crashes |
| **Multi-Timeframe** | EMA de confirmación en HTF filtra señales contra-tendencia | Reduce señales falsas ~30-40% |
| **Position sizing ATR** | `PositionSizer.Calculate()` ajusta tamaño con volatilidad | Menor exposición en mercados agitados, mayor en tranquilos |
| **Gestión de riesgo multinivel** | SL % + SL ATR + trailing + TP escalado + circuit breaker + drawdown + esperanza matemática | Múltiples capas de protección de capital |
| **Confirmación multi-indicador** | `CountConfirmations` requiere consenso configurable | Filtra señales débiles de un solo indicador |
| **Hot-reload** | `ReloadConfigAsync` reconstruye indicadores sin reiniciar | Permite ajustes en tiempo real |
| **Persistencia de estado** | Indicadores se guardan/restauran desde Redis | Reconexiones sin perder warm-up |

### 1.3 Mejoras ya implementadas (EST-1 a EST-9)

> ✅ Todas aplicadas en código. Referencia rápida:

| ID | Mejora | Severidad |
|----|--------|-----------|
| EST-1 | Prioridad de generadores adaptada al régimen (Trending vs Ranging) | 🔴 Crítico |
| EST-2 | RSI con 3 modos: conservador, agresivo, divergencia | 🔴 Crítico |
| EST-3 | Bollinger squeeze → breakout detection | 🔴 Crítico |
| EST-4 | TP escalonado (TP1 + TP2 + safety net) | 🔴 Crítico |
| EST-5 | MACD filtro de fuerza de histograma | 🟡 Importante |
| EST-6 | Volume SMA como confirmador (ratio ≥ 1.5) | 🟡 Importante |
| EST-7 | EMA crossover (EMA rápida/lenta en vez de precio/EMA) | 🟡 Importante |
| EST-8 | Fibonacci contextual según régimen | 🟡 Importante |
| EST-9 | Linear Regression minRSquared subido a 0.7 | 🟡 Importante |

---

### 1.4 🆕 Nuevos problemas detectados (EST-10 a EST-19)

> Hallazgos del análisis de código actual. Ordenados por impacto en rentabilidad.

#### 🔴 CRÍTICO — Impacto directo en ganancias/pérdidas

**EST-10: ~~TP escalonado se dispara en cascada (sin tracking de nivel alcanzado)~~ ✅ IMPLEMENTADO**

- **Solución aplicada**: `Position` tiene `TakeProfit1Hit`, `TakeProfit2Hit` (bool) y `PartialRealizedPnL` (decimal). `CalculateScaledTakeProfitQuantity` verifica flags antes de disparar — cada nivel se dispara exactamente una vez. `Position.ReduceQuantity()` permite cierres parciales manteniendo la posición abierta. `OrderSyncHandler` distingue cierre parcial (sell qty < position qty) de cierre total. `UnrealizedPnL` y `Close()` incluyen `PartialRealizedPnL`.
- **Archivos**: `Position.cs`, `RuleEngine.cs`, `OrderSyncHandler.cs`, `PositionConfiguration.cs`
- **Migración**: `AddScaledTakeProfitTracking`

**EST-11: ~~RSI divergencia (mode=2) usa EMA como proxy de precio~~ ✅ IMPLEMENTADO**

- **Solución aplicada**: Nuevo campo `_lastClosePrice` en `DefaultTradingStrategy` actualizado en `ProcessKlineAsync`, `WarmUpPrice` y `WarmUpOhlc`. `TryRsiSignal` mode 2 ahora usa `_lastClosePrice` directamente en vez de `ema.Calculate()`. La divergencia funciona sin dependencia del indicador EMA y sin lag.
- **Archivos**: `DefaultTradingStrategy.cs`

**EST-12: ~~ADX calcula Directional Movement sin OHLC — precisión reducida~~ ✅ IMPLEMENTADO**

- **Solución aplicada**: `AdxIndicator` ahora implementa `IOhlcIndicator`. `UpdateOhlc(high, low, close)` calcula +DM/-DM estándar (`CurrentHigh − PreviousHigh`, `PreviousLow − CurrentLow`) y True Range real (`max(H-L, |H-prevClose|, |L-prevClose|)`). `Update(decimal)` se mantiene como fallback con aproximación close-only. `ProcessKlineAsync` ya detecta `IOhlcIndicator` automáticamente y alimenta OHLC al ADX. Serialización/deserialización actualizados con campos `_previousHigh`, `_previousLow`, `_previousClose` (retrocompatible con `_previousPrice` anterior).
- **Archivos**: `AdxIndicator.cs`

#### 🟡 IMPORTANTE — Incrementan win rate y profit factor

**EST-13: ~~Umbral de volumen fijo (1.5×) no se adapta al activo ni al timeframe~~ ✅ IMPLEMENTADO**

- **Solución aplicada**: La factoría `IndicatorConfig.VolumeSma(period, minRatio)` ahora acepta un parámetro `minRatio` (default 1.5). En `CountConfirmations`, se lee `minRatio` del config con `GetParameter("minRatio", 1.5m)` en vez del hardcoded `1.5m`. Para BTC/1H se puede configurar `minRatio: 1.8`, para altcoins/5m `minRatio: 1.2`. Sin cambio de config, el comportamiento es idéntico al anterior.
- **Archivos**: `IndicatorConfig.cs`, `DefaultTradingStrategy.cs`

**EST-14: ~~Bollinger signal pierde señales de breakout en Trending~~ ✅ IMPLEMENTADO**

- **Solución aplicada**: `BollingerBandsIndicator` ahora rastrea `_candlesSinceSqueezeRelease` (incrementado en cada `Update`, reseteado a 0 cuando `SqueezeReleased` es `true`). Nuevo método `WasSqueezeReleasedRecently(maxCandles)` devuelve `true` si el squeeze se liberó en las últimas N velas. `TryBollingerSignal` usa `bbGen.SqueezeReleased || bbGen.WasSqueezeReleasedRecently(3)` para capturar breakouts que tardan hasta 3 velas en desarrollarse.
- **Archivos**: `BollingerBandsIndicator.cs`, `DefaultTradingStrategy.cs`

**EST-15: ~~No hay filtro de correlación BTC para altcoins~~ ✅ IMPLEMENTADO**

- **Solución aplicada**: Para estrategias que operan altcoins (símbolo no empieza con "BTC"), se crea una EMA dedicada a BTCUSDT. `ITradingStrategy` tiene nuevos métodos `ProcessBtcKline` e `IsBtcAligned`. `StrategyEngine` suscribe a klines de BTCUSDT (usa `ConfirmationTimeframe` o 4H por defecto), ejecuta warm-up con klines históricas, y un loop dedicado `ProcessBtcKlinesLoopAsync`. Antes de ejecutar una señal Buy en altcoin, verifica `IsBtcAligned(Buy)` → BTC debe estar sobre su EMA. Para pares BTC, el filtro es transparente (siempre `true`).
- **Archivos**: `ITradingStrategy.cs`, `DefaultTradingStrategy.cs`, `StrategyEngine.cs`

**EST-16: ~~TP escalonado usa porcentajes fijos — no se adapta a volatilidad~~ ✅ IMPLEMENTADO**

- **Solución aplicada**: `RiskConfig` tiene nuevas propiedades `TakeProfit1AtrMultiplier` y `TakeProfit2AtrMultiplier` (default 0 = deshabilitado). En `RuleEngine.EvaluateExitRulesAsync`, cuando ATR está disponible y los multipliers > 0, los umbrales de TP se calculan dinámicamente: `TP = (ATR × multiplier / entryPrice) × 100%`. Si ATR no está disponible o multipliers = 0, se usan los porcentajes fijos como fallback. `CalculateScaledTakeProfitQuantity` ahora recibe los umbrales calculados. `UseScaledTakeProfit` es `true` si `TakeProfit1Percent > 0 || TakeProfit1AtrMultiplier > 0`. `RiskConfigDto` actualizado con todos los campos para persistencia JSON completa.
- **Archivos**: `RiskConfig.cs`, `RuleEngine.cs`, `TradingStrategyConfiguration.cs`

#### 🟢 MEJORAS DE CALIDAD — Incrementan consistencia y reducen drawdown

**EST-17: ~~No hay re-entrada inteligente tras stop-loss~~ ✅ IMPLEMENTADO**

- **Solución aplicada**: `ITradingStrategy.NotifyStopLossHit()` notifica a la estrategia cuando se ejecuta un SL. `DefaultTradingStrategy` activa `_reEntryMode` si `IsTrendIntact()` retorna `true` (al menos 2 de 3 indicadores ADX/EMA/MACD confirman tendencia alcista). En modo re-entrada, el cooldown se reduce a 25% del normal. El modo se desactiva automáticamente al generar la siguiente señal. `StrategyEngine.ProcessSingleTickAsync` detecta SL (PnL ≤ -StopLoss%) y llama `NotifyStopLossHit()`.
- **Archivos**: `ITradingStrategy.cs`, `DefaultTradingStrategy.cs`, `StrategyEngine.cs`

**EST-18: ~~Sell proactivo en Spot desaprovechado~~ ✅ IMPLEMENTADO**

- **Solución aplicada**: En `StrategyEngine.ProcessSingleKlineAsync`, cuando una señal Sell llega y hay posición Long abierta **con ganancia** (`UnrealizedPnLPercent > 0`), se ejecuta cierre proactivo directo sin pasar por filtros de confirmación HTF/BTC (que bloquearían Sells en tendencia alcista). La posición se cierra completa con orden Market. Si la posición tiene pérdida, la señal Sell se descarta (el SL/trailing se encargarán). Diseño extensible: en Futuros, las señales Sell sin posición Long podrán abrir shorts en vez de descartarse.
- **Archivos**: `StrategyEngine.cs`

**EST-19: ~~Zona ambigua de régimen ADX (20-25) genera indecisión~~ ✅ IMPLEMENTADO**

- **Solución aplicada**: Nuevo método `ApplyRegimeHysteresis` en `DefaultTradingStrategy` aplicado después de `MarketRegimeDetector.Detect`. Usa 2 puntos de ADX como buffer: si el régimen anterior era Trending, se mantiene hasta que ADX caiga debajo de `AdxRangingThreshold - 2`; si era Ranging, se mantiene hasta que ADX suba sobre `AdxTrendingThreshold + 2`. HighVolatility y Unknown no tienen histéresis (cambian inmediatamente). No se modificó `MarketRegimeDetector` (sigue siendo stateless).
- **Archivos**: `DefaultTradingStrategy.cs`

---

### 1.5 Tabla resumen de impacto esperado

| ID | Mejora | Impacto en Win Rate | Impacto en Profit Factor | Complejidad | Estado |
|----|--------|--------------------:|-------------------------:|:-----------:|:------:|
| EST-10 | TP tracking | +0% (corrige bug) | +5-10% (menos comisiones) | 🟢 Baja | ✅ |
| EST-11 | RSI divergencia real | +3-5% | +5-8% | 🟢 Baja | ✅ |
| EST-12 | ADX con OHLC | +2-4% | +3-5% | 🟡 Media | ✅ |
| EST-13 | Volume configurable | +1-3% | +2-4% | 🟢 Baja | ✅ |
| EST-14 | BB breakout buffer | +2-4% | +4-7% | 🟢 Baja | ✅ |
| EST-15 | Correlación BTC | +5-10% (altcoins) | +8-15% (altcoins) | 🟡 Media | ✅ |
| EST-16 | TP dinámico ATR | +1-2% | +5-10% | 🟡 Media | ✅ |
| EST-17 | Re-entrada post-SL | +3-5% | +5-8% | 🟡 Media | ✅ |
| EST-18 | Sell proactivo | +2-3% | +3-5% | 🟢 Baja (config) | ✅ |
| EST-19 | Histéresis régimen | +2-4% | +3-5% | 🟢 Baja | ✅ |

**Prioridad de implementación**: ~~EST-10~~ → ~~EST-11~~ → ~~EST-12~~ → ~~EST-19~~ → ~~EST-14~~ → ~~EST-15~~ → ~~EST-16~~ → ~~EST-13~~ → ~~EST-17~~ → ~~EST-18~~ ✅ **TODAS COMPLETADAS**

---

## 2. Estrategias por tipo de mercado

> **Contexto Binance Spot**: Solo Long (Buy/Sell). Sin shorts. Comisión 0.1% por operación (0.075% con BNB). Round-trip mínimo: 0.2%. Esto significa que toda estrategia necesita targets mínimos de **0.5%** para cubrir fees + slippage y tener margen de ganancia.

### 📗 2.1 ESTRATEGIA ALCISTA — "Trend Rider"

**Filosofía**: En mercado alcista, la mejor operación es montar la tendencia y dejarla correr. No predecir techos. Entrar en pullbacks dentro de la tendencia, no en el breakout inicial (que tiene más riesgo de falso breakout).

**Cuándo activar**: `MarketRegime.Trending` + ADX > 25 + `IsBullish` (+DI > -DI)
**Cuándo desactivar**: ADX < 20 o -DI > +DI por 3+ velas consecutivas

#### Indicadores y roles

| Indicador | Config | Rol | Justificación |
|-----------|--------|-----|---------------|
| ADX(14) | threshold=25 | Filtro de régimen | ADX > 25 = tendencia fuerte. +DI > -DI = dirección alcista. Sin esto, no hay tendencia que seguir |
| EMA(21) + EMA(9) | `crossoverPeriod: 9` | Generador primario | Golden Cross (EMA9 > EMA21) confirma momentum alcista. Se prioriza sobre MACD en trending porque da señales más limpias |
| MACD(12,26,9) | `minHistogramStrength: 0.5` | Confirmador + Generador secundario | Histograma > 0 confirma momentum. El filtro de fuerza descarta cruces débiles que producen whipsaws |
| ATR(14) | `multiplier: 2.0` | Position sizing + SL dinámico | Adapta el tamaño de posición y el SL a la volatilidad actual. En BTC, ATR de 1H es ~$200-800, lo que da SL de $400-1600 |
| RSI(14) | `oversold=40, overbought=80, mode=0` | Filtro de zona | En tendencia alcista: RSI 40-60 = zona de pullback (buen entry). RSI > 80 = sobreextendido (no comprar). Oversold ajustado a 40 porque en bull market RSI raramente baja de 30 |
| VolumeSMA(20) | `minRatio: 1.3` | Confirmador | En tendencia, volumen 1.3× ya es significativo (más bajo que en mean-reversion). Confirma que hay participación real detrás del movimiento |
| BB(20,2) | estándar | Confirmador | Si el precio está dentro de las bandas y no en zona de squeeze, la tendencia es saludable. Breakout de upper band + squeeze release = señal fuerte |

#### Reglas de entrada (Buy)

```
OBLIGATORIAS (todas deben cumplirse):
1. MarketRegime = Trending (ADX > 25)
2. ADX: +DI > -DI (tendencia alcista)
3. EMA(9) > EMA(21) (Golden Cross activo)

GENERADOR (al menos uno):
4a. EMA: Precio retrocede a zona EMA(21) ± 0.3% (pullback a soporte dinámico)
4b. MACD: Histograma cruza de negativo a positivo con |hist| > minHistogramStrength

CONFIRMACIÓN (MinConfirmationPercent = 60%):
5. MACD histograma > 0 (si no fue generador)
6. RSI entre 35 y 65 (zona de pullback, no sobreextendido)
7. Volumen ≥ 1.3× promedio
8. Precio > EMA(21) (dentro de la tendencia)

HTF CONFIRMACIÓN:
9. EMA(20) en 4H: precio de cierre 4H > EMA(20) del 4H
```

#### Reglas de salida

| Tipo | Condición | Acción |
|------|-----------|--------|
| **SL ATR dinámico** | Precio < Entry − (ATR × 2.0) | Cerrar 100% (con trailing desde peak si UseTrailingStop=true) |
| **SL porcentual (safety net)** | PnL ≤ −3% | Cerrar 100% (respaldo si ATR es inválido) |
| **Trailing stop ATR** | Precio < PeakPrice − (ATR × 1.5) | Cerrar 100% (protege ganancias) |
| **TP1 escalonado** | PnL ≥ 2.5% (o ATR × 1.5) | Cerrar 40% de la posición |
| **TP2 escalonado** | PnL ≥ 5.0% (o ATR × 3.0) | Cerrar 35% del remanente |
| **TP simple (safety net)** | PnL ≥ 8.0% | Cerrar 100% restante |
| **Salida por régimen** | ADX cae < 20 (ExitOnRegimeChange=true) | Cerrar 100% (tendencia muerta) |
| **Time-based exit** | > 24 velas de 1H sin alcanzar TP1 | Cerrar 100% (el pullback no continuó) |
| **Sell proactivo** | RSI > 80 + posición con PnL > 1% | Cerrar 60% (sobreextendido, asegurar ganancia) |

#### RiskConfig completo

```json
{
  "MaxOrderAmountUsdt": 200,
  "MaxDailyLossUsdt": 500,
  "StopLossPercent": 3.0,
  "TakeProfitPercent": 8.0,
  "MaxOpenPositions": 2,
  "UseAtrSizing": true,
  "RiskPercentPerTrade": 1.0,
  "AtrMultiplier": 2.0,
  "UseTrailingStop": true,
  "TrailingStopPercent": 1.5,
  "MaxSpreadPercent": 0.5,
  "ConfirmationEmaPeriod": 20,
  "SignalCooldownPercent": 50,
  "AdxTrendingThreshold": 25,
  "AdxRangingThreshold": 20,
  "HighVolatilityBandWidthPercent": 0.08,
  "HighVolatilityAtrPercent": 0.03,
  "MinConfirmationPercent": 60,
  "TakeProfit1Percent": 2.5,
  "TakeProfit1ClosePercent": 40,
  "TakeProfit2Percent": 5.0,
  "TakeProfit2ClosePercent": 35,
  "MaxPositionDurationCandles": 24,
  "ExitOnRegimeChange": true
}
```

#### Indicadores JSON para creación de estrategia

```json
[
  { "type": "ADX", "parameters": { "period": 14 } },
  { "type": "EMA", "parameters": { "period": 21, "crossoverPeriod": 9 } },
  { "type": "MACD", "parameters": { "fastPeriod": 12, "slowPeriod": 26, "signalPeriod": 9, "minHistogramStrength": 0.5 } },
  { "type": "ATR", "parameters": { "period": 14 } },
  { "type": "RSI", "parameters": { "period": 14, "oversold": 40, "overbought": 80, "mode": 0 } },
  { "type": "Volume", "parameters": { "period": 20 } },
  { "type": "BollingerBands", "parameters": { "period": 20, "stdDev": 2 } }
]
```

**Timeframe**: 1H primario, 4H confirmación HTF
**Pares recomendados**: BTCUSDT, ETHUSDT (alta liquidez, spreads mínimos)
**Métricas objetivo**: Win rate 40-50%, Profit Factor > 1.8, R:R promedio 1:2.5, Max Drawdown < 12%
**Frecuencia esperada**: 2-5 trades/semana

---

### 📕 2.2 ESTRATEGIA BAJISTA — "Defensive Bottom Catcher"

> **⚠ Binance Spot = Solo Long.** No podemos shortear. Esta estrategia tiene DOS modos operativos:
> - **Modo Defensivo**: Proteger capital, NO abrir posiciones, cerrar existentes agresivamente.
> - **Modo Contrarian**: Detectar agotamiento bajista para comprar en fondos con alta convicción.

**Filosofía**: "Cash is a position". En mercado bajista, la mejor operación es NO operar. Solo entrar cuando hay señales extremas de agotamiento (capitulación). Entrar con position size reducido (50% del normal) y SL muy ajustado. Si no funciona rápido, salir.

**Cuándo activar modo defensivo**: ADX > 25 + `-DI > +DI` (tendencia bajista confirmada)
**Cuándo activar modo contrarian**: RSI < 25 + señales de agotamiento + volumen climático
**Cuándo desactivar**: ADX < 20 o +DI > -DI (tendencia bajista terminó)

#### Indicadores y roles

| Indicador | Config | Rol | Justificación |
|-----------|--------|-----|---------------|
| ADX(14) | threshold=25 | Clasificador de régimen + dirección | -DI > +DI confirma presión vendedora. ADX alto = caída fuerte. ADX bajando = desaceleración |
| RSI(14) | `oversold=25, overbought=65, mode=1` | Generador primario | Mode 1 (agresivo): señal al ENTRAR en zona extrema (RSI ≤ 25). Oversold=25 (no 30) porque en bear market los fondos son más profundos |
| MACD(12,26,9) | `minHistogramStrength: 0` | Confirmador de agotamiento | Histograma negativo que se reduce (divergencia positiva) = la caída pierde fuerza. Es la señal más confiable de fondo |
| BB(20,2.5) | `stdDev: 2.5` | Confirmador de extensión | stdDev=2.5 (no 2.0) porque queremos captar extremos. Precio < Lower Band 2.5σ = estadísticamente raro (~1% del tiempo) |
| ATR(14) | `multiplier: 1.0` | SL ajustado | Multiplicador 1.0 (no 2.0) porque la operación contrarian debe funcionar rápido o salir |
| VolumeSMA(20) | `minRatio: 2.0` | Confirmador de capitulación | Ratio ≥ 2.0 (el doble de lo normal) = selling climax. Los fondos importantes ocurren con volumen extremo |
| Fibonacci(50) | estándar | Confirmador de soporte | Niveles 0.618 y 0.786 son soportes clave. Si el precio coincide con un Fib + RSI extremo, la probabilidad de rebote sube |

#### Reglas de entrada (Buy — solo en modo contrarian)

```
BLOQUEO PREVIO (si alguno se cumple, NO operar):
0a. Si hay posiciones Long abiertas con PnL < -1%, cerrarlas con SL agresivo antes de abrir nuevas
0b. Si la pérdida diaria supera el 60% del MaxDailyLossUsdt → NO abrir nada

GENERADOR (señal de fondo):
1. RSI(14) ≤ 25 (sobreventa extrema — mode 1 genera señal al entrar en zona)

CONFIRMACIÓN (MinConfirmationPercent = 75% — umbral alto porque es operación contrarian):
2. MACD: histograma negativo se reduce vs vela anterior (|hist actual| < |hist anterior|) = desaceleración
3. BB: precio ≤ Lower Band (2.5σ) — extensión extrema
4. Volumen ≥ 2.0× promedio (selling climax / capitulación)
5. Fibonacci: precio cerca de nivel 0.618 o 0.786

HTF CONFIRMACIÓN (OBLIGATORIA para contrarian):
6. En 4H: última vela cerró positiva (primer signo de recuperación)
   O en 1D: RSI(14) del daily < 30 (sobreventa en timeframe macro)
```

#### Reglas de salida

| Tipo | Condición | Acción |
|------|-----------|--------|
| **SL ATR ajustado** | Precio < Entry − (ATR × 1.0) | Cerrar 100%. SL ajustado porque el fondo debe funcionar rápido |
| **SL porcentual** | PnL ≤ −1.5% | Cerrar 100%. Más agresivo que en trend-following |
| **TP1 conservador** | PnL ≥ 2.0% | Cerrar 50%. En bear rallies, asegurar ganancia rápido |
| **TP2** | PnL ≥ 4.0% | Cerrar 40% del remanente |
| **TP simple** | PnL ≥ 6.0% | Cerrar 100% restante. No ser avaricioso en bear market |
| **Time-based exit** | > 6 velas (6H) sin alcanzar TP1 | Cerrar 100%. Si en 6 horas no reacciona, el fondo no era real |
| **Régimen cambia a Trending alcista** | ADX > 25 + +DI > -DI | Mantener posición (transición a Trend Rider) o cerrar y re-abrir con config alcista |

#### RiskConfig completo

```json
{
  "MaxOrderAmountUsdt": 100,
  "MaxDailyLossUsdt": 250,
  "StopLossPercent": 1.5,
  "TakeProfitPercent": 6.0,
  "MaxOpenPositions": 1,
  "UseAtrSizing": true,
  "RiskPercentPerTrade": 0.5,
  "AtrMultiplier": 1.0,
  "UseTrailingStop": false,
  "TrailingStopPercent": 0,
  "MaxSpreadPercent": 0.3,
  "ConfirmationEmaPeriod": 20,
  "SignalCooldownPercent": 70,
  "AdxTrendingThreshold": 25,
  "AdxRangingThreshold": 20,
  "HighVolatilityBandWidthPercent": 0.10,
  "HighVolatilityAtrPercent": 0.04,
  "MinConfirmationPercent": 75,
  "TakeProfit1Percent": 2.0,
  "TakeProfit1ClosePercent": 50,
  "TakeProfit2Percent": 4.0,
  "TakeProfit2ClosePercent": 40,
  "MaxPositionDurationCandles": 6,
  "ExitOnRegimeChange": false
}
```

#### Indicadores JSON

```json
[
  { "type": "ADX", "parameters": { "period": 14 } },
  { "type": "RSI", "parameters": { "period": 14, "oversold": 25, "overbought": 65, "mode": 1 } },
  { "type": "MACD", "parameters": { "fastPeriod": 12, "slowPeriod": 26, "signalPeriod": 9, "minHistogramStrength": 0 } },
  { "type": "BollingerBands", "parameters": { "period": 20, "stdDev": 2.5 } },
  { "type": "ATR", "parameters": { "period": 14 } },
  { "type": "Volume", "parameters": { "period": 20 } },
  { "type": "Fibonacci", "parameters": { "period": 50 } }
]
```

**Timeframe**: 1H primario, 4H confirmación HTF
**Pares recomendados**: BTCUSDT (el mercado sigue a BTC en caídas; operar el líder)
**Métricas objetivo**: Win rate 35-45%, Profit Factor > 1.5, R:R promedio 1:2, Max Drawdown < 8%
**Frecuencia esperada**: 1-3 trades/semana (baja frecuencia, alta selectividad)

**Notas clave para mercado bajista**:
- `RiskPercentPerTrade: 0.5%` (mitad del normal) — capital preservation es prioridad
- `MaxOpenPositions: 1` — una sola apuesta a la vez
- `MaxPositionDurationCandles: 6` — si no funciona rápido, salir. Los bear rallies son cortos
- `UseTrailingStop: false` — en bear market, el trailing stop se activa demasiado pronto por la alta volatilidad a la baja
- `HighVolatilityBandWidthPercent: 0.10` y `HighVolatilityAtrPercent: 0.04` — umbrales más altos porque en bear market la volatilidad base es mayor

---

### 📘 2.3 ESTRATEGIA LATERAL — "Range Scalper"

**Filosofía**: Comprar en soporte (parte baja del rango) y vender en resistencia (parte alta). Es la estrategia con **mayor win rate** porque los rangos son predecibles. La clave es SL pequeño y TP moderado. Si el rango se rompe, salir inmediatamente.

**Cuándo activar**: `MarketRegime.Ranging` (ADX < 20, o ADX 20-25 con BandWidth < 0.04)
**Cuándo desactivar**: ADX > 25 (el rango se rompió → transicionar a Trend Rider o Defensive)

#### Indicadores y roles

| Indicador | Config | Rol | Justificación |
|-----------|--------|-----|---------------|
| ADX(14) | ranging < 20 | Filtro obligatorio | ADX < 20 confirma ausencia de tendencia. Es la condición sine qua non para mean-reversion |
| BB(20,2.0) | estándar | Generador primario | Lower Band = soporte dinámico del rango, Upper Band = resistencia. Middle Band = target de TP1 |
| RSI(14) | `oversold=30, overbought=70, mode=0, confirmationZone=10` | Generador secundario + Confirmador | Mode 0 (conservador): espera cruce de vuelta de zona extrema (más seguro en lateral). confirmationZone=10 → confirma Buy si RSI ≤ 40 |
| LinearRegression(20) | `minRSquared: 0.3` | Confirmador negativo | R² < 0.3 confirma que NO hay tendencia lineal. Si R² > 0.7, es tendencia encubierta — no operar mean-reversion |
| Fibonacci(50) | estándar | Confirmador | En rango, los niveles 0.618 y 0.786 actúan como soporte/resistencia adicional |
| VolumeSMA(20) | `minRatio: 1.2` | Confirmador | En lateral, volumen 1.2× ya es significativo. Confirma que hay interés de compra en la zona de soporte |
| ATR(14) | estándar | Referencia para SL | Aunque no se usa ATR sizing (posiciones fijas en lateral), el ATR ayuda a definir si el SL es razonable vs la volatilidad |

#### Reglas de entrada (Buy)

```
OBLIGATORIAS:
1. MarketRegime = Ranging (ADX < 20)
2. LinearRegression R² < 0.4 (confirma ausencia de tendencia)

GENERADOR (al menos uno):
3a. BB: Precio ≤ Lower Band (precio en zona de soporte del rango)
3b. RSI: RSI cruza de vuelta por encima de 30 (sale de zona de sobreventa)

CONFIRMACIÓN (MinConfirmationPercent = 50% — menos estricto porque el régimen ya filtra):
4. RSI en zona ≤ 40 (si BB fue generador)
5. BB: precio cerca de Lower Band (si RSI fue generador)
6. Fibonacci: precio cerca de nivel 0.618 o 0.786
7. Volumen ≥ 1.2× promedio

SIN HTF CONFIRMACIÓN (no necesaria en lateral — los rangos son timeframe-específicos)
```

#### Reglas de salida

| Tipo | Condición | Acción |
|------|-----------|--------|
| **SL fijo** | PnL ≤ −1.5% | Cerrar 100%. En rango, movimientos > 1.5% contra la posición = el rango se rompió |
| **TP1 (BB Middle)** | Precio ≥ BB Middle Band (SMA20) | Cerrar 60%. La banda media es el target natural de mean-reversion |
| **TP2 (BB Upper)** | Precio ≥ BB Upper Band | Cerrar 100% restante. Llegó al extremo opuesto del rango |
| **TP simple (safety)** | PnL ≥ 3.0% | Cerrar 100%. Safety net si las bandas se expanden |
| **Salida por ruptura de rango** | ADX sube > 25 (ExitOnRegimeChange=true) | Cerrar 100% inmediatamente. El rango se rompió, mean-reversion ya no funciona |
| **Time-based exit** | > 12 velas (12H) sin alcanzar TP1 | Cerrar 100%. Si en medio rango no avanza, la tesis falló |
| **RSI overbought** | RSI > 70 + posición con PnL > 0.5% | Cerrar 60%. Sobrecompra en lateral = reversión inminente |

#### RiskConfig completo

```json
{
  "MaxOrderAmountUsdt": 150,
  "MaxDailyLossUsdt": 400,
  "StopLossPercent": 1.5,
  "TakeProfitPercent": 3.0,
  "MaxOpenPositions": 2,
  "UseAtrSizing": false,
  "RiskPercentPerTrade": 1.0,
  "AtrMultiplier": 1.5,
  "UseTrailingStop": false,
  "TrailingStopPercent": 0,
  "MaxSpreadPercent": 0.3,
  "ConfirmationEmaPeriod": 20,
  "SignalCooldownPercent": 30,
  "AdxTrendingThreshold": 25,
  "AdxRangingThreshold": 20,
  "HighVolatilityBandWidthPercent": 0.06,
  "HighVolatilityAtrPercent": 0.025,
  "MinConfirmationPercent": 50,
  "TakeProfit1Percent": 1.5,
  "TakeProfit1ClosePercent": 60,
  "TakeProfit2Percent": 2.5,
  "TakeProfit2ClosePercent": 100,
  "MaxPositionDurationCandles": 12,
  "ExitOnRegimeChange": true
}
```

#### Indicadores JSON

```json
[
  { "type": "ADX", "parameters": { "period": 14 } },
  { "type": "BollingerBands", "parameters": { "period": 20, "stdDev": 2 } },
  { "type": "RSI", "parameters": { "period": 14, "oversold": 30, "overbought": 70, "mode": 0, "confirmationZone": 10 } },
  { "type": "LinearRegression", "parameters": { "period": 20, "minRSquared": 0.3 } },
  { "type": "Fibonacci", "parameters": { "period": 50 } },
  { "type": "Volume", "parameters": { "period": 20 } },
  { "type": "ATR", "parameters": { "period": 14 } }
]
```

**Timeframe**: 15min o 1H primario. Sin confirmación HTF
**Pares recomendados**: ETHUSDT, BNBUSDT (períodos de consolidación frecuentes), BTCUSDT en fases de acumulación
**Métricas objetivo**: Win rate 55-65%, Profit Factor > 1.6, R:R promedio 1:1.5, Max Drawdown < 8%
**Frecuencia esperada**: 5-12 trades/semana

**Notas clave para mercado lateral**:
- `UseAtrSizing: false` — position size fijo porque la volatilidad es baja y predecible
- `UseTrailingStop: false` — en lateral, el trailing stop se activa por ruido normal del rango
- `HighVolatilityBandWidthPercent: 0.06` — umbral más bajo porque si el BandWidth sube a 6% ya indica expansión peligrosa
- `SignalCooldownPercent: 30` — cooldown más corto (30% de la vela) para captar más oportunidades en el rango
- `MaxOpenPositions: 2` — se pueden tener 2 posiciones en lateral (ej: ETHUSDT + BNBUSDT)
- `TakeProfit1Percent: 1.5%` y `TakeProfit2Percent: 2.5%` — targets conservadores pero alcanzables en un rango típico de 3-5%

---

## 3. Comparativa de estrategias

| Aspecto | 📗 Trend Rider | 📕 Bottom Catcher | 📘 Range Scalper |
|---------|:--------------:|:------------------:|:----------------:|
| **Régimen** | Trending + Bullish | Trending + Bearish | Ranging |
| **Win rate esperado** | 40-50% | 35-45% | 55-65% |
| **R:R promedio** | 1:2.5 | 1:2.0 | 1:1.5 |
| **Profit Factor** | > 1.8 | > 1.5 | > 1.6 |
| **Max Drawdown** | < 12% | < 8% | < 8% |
| **Frecuencia** | 2-5/semana | 1-3/semana | 5-12/semana |
| **Risk per trade** | 1.0% | 0.5% | 1.0% |
| **SL típico** | 3% o ATR×2 | 1.5% o ATR×1 | 1.5% fijo |
| **TP típico** | 2.5→5→8% | 2→4→6% | 1.5→2.5→3% |
| **Trailing stop** | ✅ Sí (1.5%) | ❌ No | ❌ No |
| **ATR sizing** | ✅ Sí | ✅ Sí (conservador) | ❌ No |
| **HTF confirmación** | ✅ 4H | ✅ 4H | ❌ No necesario |
| **Min confirmación** | 60% | 75% | 50% |
| **MaxPositions** | 2 | 1 | 2 |
| **Time exit (velas)** | 24 | 6 | 12 |
| **ExitOnRegimeChange** | ✅ Sí | ❌ No (ya es bear) | ✅ Sí (crítico) |
| **Comisiones estimadas** | ~0.2% × 3 ops = 0.6%/sem | ~0.2% × 2 ops = 0.4%/sem | ~0.2% × 8 ops = 1.6%/sem |
| **Mejor timeframe** | 1H | 1H | 15min-1H |
| **Mejor par** | BTCUSDT, ETHUSDT | BTCUSDT | ETHUSDT, BNBUSDT |

---

## 4. Plan de validación en Testnet

### 4.1 Orden de activación

| Fase | Duración | Estrategia | Par | Capital virtual |
|------|----------|-----------|-----|---------------:|
| **1** | 2 semanas | Range Scalper | ETHUSDT 1H | $200 USDT |
| **2** | 2 semanas | Trend Rider | BTCUSDT 1H | $200 USDT |
| **3** | 2 semanas | Bottom Catcher | BTCUSDT 1H | $100 USDT |
| **4** | 4 semanas | Las 3 simultáneas | Mixto | $500 USDT |

> Range Scalper va primero porque genera más trades y permite validar el sistema más rápido.

### 4.2 Métricas de validación (mínimos para pasar a producción)

| Métrica | Mínimo | Óptimo | Medición |
|---------|--------|--------|----------|
| **Win Rate** | > 40% global | > 50% | Trades ganadores / Total trades |
| **Profit Factor** | > 1.3 | > 1.8 | Σ ganancias / Σ pérdidas |
| **Sharpe Ratio** | > 0.8 | > 1.5 | (Retorno medio − Rf) / σ retornos |
| **Max Drawdown** | < 15% | < 10% | Mayor caída desde peak de equity |
| **Recovery Factor** | > 1.5 | > 3.0 | Net Profit / Max Drawdown |
| **Avg R:R realizado** | > 1.2:1 | > 2.0:1 | Ganancia promedio / Pérdida promedio |
| **Esperanza matemática** | > 0 | > 0.005 | (WR × AvgWin) − (LR × AvgLoss) |
| **Trades/semana** | ≥ 3 | 5-15 | Frecuencia operativa total |
| **Consecutivos perdedores** | < 8 | < 5 | Máxima racha de trades perdedores |
| **% días con ganancia** | > 45% | > 55% | Días verdes / Total días operados |
| **Comisiones / Ganancia** | < 25% | < 15% | Total fees / Total profit bruto |

### 4.3 Criterios de fallo y acción correctiva

| Señal de alerta | Acción |
|----------------|--------|
| Max Drawdown > 15% | Pausar estrategia, revisar parámetros de SL |
| Win Rate < 30% después de 50+ trades | Desactivar, recalibrar umbrales de indicadores |
| Profit Factor < 1.0 (perdiendo dinero) | Desactivar inmediatamente |
| Esperanza matemática < 0 | El sistema ya lo bloquea automáticamente (RiskManager) |
| > 10 trades/día en Range Scalper | Revisar cooldown y MinConfirmation (sobreoperación) |
| Comisiones > 30% de ganancia bruta | Aumentar TP mínimo o reducir frecuencia |
| 3+ trades consecutivos con slippage > 0.3% | Revisar MaxSpreadPercent y liquidez del par |

---

## 5. Configuraciones exactas para backtest inicial

### Config 1: Trend Rider — BTCUSDT 1H

```
Indicadores: EMA(21, crossover=9), MACD(12,26,9, minHist=0.5), ADX(14), ATR(14), 
             RSI(14, os=40, ob=80, mode=0), VolumeSma(20), BB(20,2)
ConfirmationTimeframe: FourHours
RiskConfig: SL=3%, TP=8%, TP1=2.5%/40%, TP2=5%/35%, ATR sizing ON, Risk=1%, 
            ATR mult=2.0, Trailing=1.5%, MinConfirm=60%, ExitOnRegime=true, MaxDuration=24
```

### Config 2: Range Scalper — ETHUSDT 1H

```
Indicadores: BB(20,2), RSI(14, os=30, ob=70, mode=0, confZone=10), ADX(14), 
             Fibonacci(50), LinReg(20, minR²=0.3), VolumeSma(20), ATR(14)
ConfirmationTimeframe: null
RiskConfig: SL=1.5%, TP=3%, TP1=1.5%/60%, TP2=2.5%/100%, ATR sizing OFF, 
            MinConfirm=50%, ExitOnRegime=true, MaxDuration=12, Cooldown=30%
```

### Config 3: Bottom Catcher — BTCUSDT 1H

```
Indicadores: RSI(14, os=25, ob=65, mode=1), MACD(12,26,9, minHist=0), ADX(14), 
             BB(20,2.5), ATR(14), VolumeSma(20), Fibonacci(50)
ConfirmationTimeframe: FourHours
RiskConfig: SL=1.5%, TP=6%, TP1=2%/50%, TP2=4%/40%, ATR sizing ON, Risk=0.5%, 
            ATR mult=1.0, MinConfirm=75%, MaxPositions=1, MaxDuration=6, Cooldown=70%
```