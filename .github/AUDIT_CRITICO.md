# TradingBot — Auditoría Crítica de Código

> **Fecha**: 2026-03-09
> **Contexto**: Revisión exhaustiva de código fuente con enfoque en bugs que afectan operación con dinero real.
> **Prioridad**: Corregir TODOS los P0 antes de operar en Testnet/Live.

---

## 📊 Resumen ejecutivo

| Categoría | Hallazgos | Estado |
|-----------|-----------|--------|
| 🔴 P0 — Bloqueantes | 4 bugs críticos | ✅ Corregidos |
| 🟠 P1 — Impacto alto | 3 problemas de diseño | ✅ Corregidos |
| 🟡 P2 — Impacto medio | 4 mejoras importantes | ✅ Corregidos |
| ⚪ P3 — Bajo riesgo | 2 mejoras de robustez | ✅ Corregidos |
| **Total** | **13 issues** | |

---

## 🔴 P0 — Bugs Críticos (operación IMPOSIBLE sin fix)

### P0-1: RiskManager — `orderAmount = 0` para órdenes Market

**Archivo**: `src/TradingBot.Application/RiskManagement/RiskManager.cs:58`

**Bug**:
```csharp
var orderAmount = order.Quantity.Value * (order.LimitPrice?.Value ?? 0m);
```

Para órdenes **Market** (la gran mayoría), `LimitPrice` es `null` → `orderAmount = Quantity × 0 = 0`.

**Impacto**:
- Validación #0 (balance): `required = 0 × 1.05 = 0` → siempre pasa
- Validación #1 (maxOrderAmount): `0 > maxOrderAmount` → siempre `false` → **BYPASSED**
- El bot puede enviar órdenes Market de CUALQUIER tamaño sin que el RiskManager las detenga

**Fix**: Necesitamos una estimación del precio para órdenes Market. La `Quantity` ya se calcula como `amountUsdt / currentPrice` en el `RuleEngine`, por lo que se puede reconstruir el valor notional usando el precio actual del mercado, o requerir que el `Order` lleve una referencia de precio estimado.

**Solución elegida**: Agregar `EstimatedPrice` al `Order` (un `Price?` opcional que se establece al crear la orden desde el `RuleEngine` con el `currentPrice`). El `RiskManager` usa `LimitPrice ?? EstimatedPrice ?? 0` para calcular el monto.

---

### P0-2: PortfolioRiskManager — misma falla `orderExposureUsdt = 0`

**Archivo**: `src/TradingBot.Application/RiskManagement/PortfolioRiskManager.cs:51`

**Bug**:
```csharp
var orderExposureUsdt = order.Quantity.Value * (order.LimitPrice?.Value ?? order.ExecutedPrice?.Value ?? 0m);
```

Para órdenes Market nuevas (antes de ejecución), tanto `LimitPrice` como `ExecutedPrice` son `null` → `orderExposureUsdt = 0`. Todas las validaciones de exposición del portafolio (Long limit, Short limit, concentración por símbolo) se bypasean.

**Impacto**: El bot puede concentrar exposición ilimitada en un solo símbolo.

**Fix**: Usar `order.EstimatedPrice` (mismo campo del P0-1).

---

### P0-3: Exchange filters — ajustes calculados pero NUNCA aplicados

**Archivo**: `src/TradingBot.Application/Services/OrderService.cs:196-234`

**Bug**:
```csharp
var (adjustedQty, adjustedPrice) = validateResult.Value;
if (adjustedQty != order.Quantity.Value || adjustedPrice != order.LimitPrice?.Value)
{
    _logger.LogDebug("Orden ajustada...");
    // ⚠️ PERO NO APLICA EL AJUSTE AL ORDER
}
return Result<bool, DomainError>.Success(true);
```

El método `ValidateExchangeFiltersAsync` calcula los valores ajustados por `stepSize`/`tickSize` y los valida, pero **nunca los escribe de vuelta en la orden**. La orden va a Binance con los valores originales sin ajustar.

**Impacto**: Binance rechaza órdenes con error `-1013` (filter failure) porque `Quantity` no es múltiplo exacto de `stepSize`.

**Fix**: Agregar método `AdjustForExchange(Quantity, Price?)` en la entidad `Order` que permita actualizar cantidad/precio antes de la ejecución. Llamarlo en `ValidateExchangeFiltersAsync` con los valores ajustados.

---

### P0-4: Short selling en Spot — posiciones fantasma

**Archivo**: `src/TradingBot.Application/Services/OrderService.cs:370-399`

**Bug conceptual**: Cuando una señal `Sell` se genera y no hay posición Long abierta para cerrar, el sistema abre una nueva posición `Side=Sell` (Short). En **Binance Spot, no se puede vender un activo que no se posee**.

**Flujo del bug**:
1. RSI cruza overbought → señal `Sell`
2. No hay posición Long abierta (nunca se compró)
3. `HandlePositionAsync` no encuentra posición opuesta → `else` → abre nueva `Position(Side=Sell)`
4. En Paper Trading: posición Short ficticia que nunca puede replicarse en Live
5. En Live: Binance rechaza con `-2010` (Insufficient balance) → la orden falla pero el bot no entiende por qué

**Impacto**:
- Paper Trading: métricas de P&L falsas (shorts ficticios pueden mostrar rentabilidad irreal)
- Live: órdenes rechazadas continuamente sin mecanismo de recuperación

**Fix**: En `HandlePositionAsync`, si la orden es `Sell` y NO hay posición Long que cerrar, NO abrir posición Short. Solo permitir `Sell` si hay posición Long existente. Para soportar shorts reales en el futuro, validar que el `TradingMode` incluya Margin/Futures.

---

## 🟠 P1 — Impacto Alto (afectan calidad de señales y operación)

### P1-1: Sin deduplicación de posiciones — compras duplicadas

**Archivo**: `src/TradingBot.Application/Strategies/StrategyEngine.cs:481-552`

**Problema**: Si el RSI cruza el umbral de oversold, genera una señal Buy. Si el precio rebota y vuelve a cruzar 1 minuto después (pasado el cooldown), genera OTRA señal Buy. No hay validación de "ya tengo una posición abierta Buy en BTCUSDT para esta estrategia".

**Impacto**: Posiciones duplicadas que multiplican la exposición más allá de lo configurado. El `MaxOpenPositions` en RiskManager cuenta TODAS las posiciones pero no previene duplicados por símbolo/dirección.

**Fix**: Antes de colocar una orden de entrada, verificar si ya existe una posición abierta del mismo símbolo y dirección para esta estrategia. Si existe, no colocar nueva orden.

---

### P1-2: Solo RSI genera señales — plantillas MACD/Bollinger/EMA no funcionan

**Archivo**: `src/TradingBot.Application/Strategies/DefaultTradingStrategy.cs:153`

**Problema**:
```csharp
if (!_indicators.TryGetValue(IndicatorType.RSI, out var rsiIndicator) || !rsiIndicator.IsReady)
    return null;
```

Sin RSI configurado, la estrategia NUNCA genera señales. La plantilla "MACD Crossover" en realidad requiere RSI + MACD como confirmador. Un usuario que configure solo EMA Crossover o solo MACD no verá ninguna señal.

**Impacto**: 3 de 4 plantillas predefinidas son misleading. El usuario piensa que MACD genera señales pero en realidad depende de RSI.

**Fix**: Implementar múltiples generadores de señales primarios:
- RSI: oversold/overbought crossover (existente)
- MACD: histogram crossover (positivo→negativo = sell, negativo→positivo = buy)
- EMA/SMA: price crossover (precio cruza EMA up = buy, down = sell)
- Bollinger: price touches bands (price ≤ lower = buy, ≥ upper = sell)

La señal se genera por el primer generador disponible en la estrategia. Los demás confirman.

---

### P1-3: Warm-up usa solo Close — ATR/Bollinger reciben datos incorrectos

**Archivo**: `src/TradingBot.Application/Strategies/StrategyEngine.cs:235-241`

**Problema**:
```csharp
var syntheticTick = new MarketTickReceivedEvent(
    config.Symbol,
    priceResult.Value, priceResult.Value, priceResult.Value, // Bid=Ask=Last=Close
    0m, DateTimeOffset.UtcNow);
```

ATR necesita High/Low para True Range. Con H=L=Close, `TR = Max(H-L, |H-C_prev|, |L-C_prev|) = 0`. ATR queda en cero durante todo el warm-up, lo que significa:
- Position sizing ATR: `stopDistance = 0 × multiplier = 0` → división por cero o fallback a maxOrderAmount
- MarketRegimeDetector: `atrPercent = 0` → no detecta alta volatilidad

**Fix**: Usar `GetKlinesAsync` en lugar de `GetHistoricalClosesAsync` para warm-up. Alimentar los indicadores con High/Low/Close reales.

---

## 🟡 P2 — Impacto Medio (performance, protección, fidelidad)

### P2-1: Comparación `==` en decimales calculados

**Archivo**: `src/TradingBot.Application/Rules/RuleEngine.cs:200`

**Problema**:
```csharp
Comparator.Equal    => actualValue.Value == leaf.Value,
Comparator.NotEqual => actualValue.Value != leaf.Value,
```

Un RSI calculado como `30.0000001` no matcheará `== 30`. Esto hace que reglas con condiciones de igualdad exacta sean prácticamente inútiles con indicadores que producen valores continuos.

**Fix**: Implementar tolerancia epsilon (`Math.Abs(a - b) < 0.0001m`) para `Equal` y `NotEqual`.

---

### P2-2: Cada tick consulta la BD para cargar la estrategia

**Archivo**: `src/TradingBot.Application/Strategies/StrategyEngine.cs:477`

**Problema**:
```csharp
var strategy = await strategyRepo.GetWithRulesAsync(runner.StrategyId, cancellationToken);
```

Con 5 estrategias y ticks cada 100ms: ~50 queries/segundo solo para cargar reglas que cambian una vez al día.

**Fix**: Cachear la `TradingStrategy` en el `StrategyRunnerState`. Solo recargar en `ReloadStrategyAsync`.

---

### P2-3: Sin Trailing Stop-Loss

**Archivo**: `src/TradingBot.Application/Rules/RuleEngine.cs:57-175`

**Problema**: Stop-loss fijo (porcentual o ATR desde precio de entrada). Si BTC sube de 60k a 80k y luego cae a 65k, el stop-loss desde 60k (ej: 2%) está en 58.8k → nunca activa → se pierde 100% de las ganancias no realizadas.

**Fix**: Implementar trailing stop que suba el nivel de stop a medida que el precio alcanza nuevos máximos. `trailingStopPrice = maxPriceSinceEntry - (ATR × multiplier)`.

---

### P2-4: Backtest no usa ATR dynamic sizing

**Archivo**: `src/TradingBot.Application/Backtesting/BacktestEngine.cs:148-153`

**Problema**: El backtest usa `maxOrderAmountUsdt` como cap pero NO implementa `PositionSizer` con ATR. Los resultados del backtest no reflejan lo que haría el bot en live con `UseAtrSizing=true`.

**Fix**: Si la estrategia tiene `UseAtrSizing=true`, aplicar `PositionSizer.Calculate()` en el backtest con el ATR calculado del warm-up.

---

## ⚪ P3 — Mejoras de Robustez

### P3-1: Esperanza matemática bloquea con solo 10 trades

**Archivo**: `src/TradingBot.Application/RiskManagement/RiskManager.cs:19`

**Problema**: `MinTradesForExpectancy = 10`. Con drawdown inicial normal (primeros 10 trades negativos por mala racha), el RiskManager bloquea la estrategia permanentemente sin recovery automático.

**Fix**: Subir a 30 trades mínimos. Agregar opción de override manual o expiración del bloqueo (ej: recalcular después de N trades adicionales).

---

### P3-2: `MaxConsecutiveErrors = 10` mata la estrategia permanentemente

**Archivo**: `src/TradingBot.Application/Strategies/StrategyEngine.cs:382-427`

**Problema**: 10 errores consecutivos (ej: Binance API caída 2 minutos) marca la estrategia como `Error`. No hay recovery automático. La estrategia queda muerta hasta intervención manual.

**Fix**: En lugar de muerte permanente, implementar cooldown progresivo (30s → 1m → 5m → 15m → 30m) con restart automático. Solo marcar como Error tras agotar todos los reintentos en un período de 1 hora.

---

## 📋 Orden de implementación

| # | ID | Descripción | Esfuerzo | Archivos a modificar |
|---|-----|-------------|----------|---------------------|
| 1 | P0-1 | RiskManager orderAmount=0 | 45 min | Order.cs, RiskManager.cs, RuleEngine.cs, tests |
| 2 | P0-2 | PortfolioRiskManager exposure=0 | 15 min | PortfolioRiskManager.cs (usa campo de P0-1) |
| 3 | P0-3 | Exchange filters no aplicados | 30 min | Order.cs, OrderService.cs, tests |
| 4 | P0-4 | Short selling en Spot | 30 min | OrderService.cs, tests |
| 5 | P1-1 | Deduplicación posiciones | 30 min | StrategyEngine.cs, tests |
| 6 | P1-2 | Generadores de señales independientes | 2h | DefaultTradingStrategy.cs, tests |
| 7 | P1-3 | Warm-up con OHLCV | 1h | StrategyEngine.cs, IMarketDataService.cs, MarketDataService.cs |
| 8 | P2-1 | Epsilon en comparadores | 15 min | RuleEngine.cs, tests |
| 9 | P2-2 | Cache de estrategia en runner | 30 min | StrategyEngine.cs |
| 10 | P2-3 | Trailing stop-loss | 1.5h | RuleEngine.cs, Position.cs, RiskConfig.cs, tests |
| 11 | P2-4 | ATR sizing en backtest | 45 min | BacktestEngine.cs, tests |
| 12 | P3-1 | Threshold esperanza matemática | 15 min | RiskManager.cs, tests |
| 13 | P3-2 | Cooldown errores consecutivos | 45 min | StrategyEngine.cs, tests |

---

## 🔧 Tests a agregar/modificar por cada fix

| Fix | Tests nuevos | Tests modificados |
|-----|-------------|-------------------|
| P0-1 | `ValidateOrder_WhenMarketOrderExceedsMax_BlocksWithEstimatedPrice` | `RiskManagerTests` helpers |
| P0-2 | (cubierto por P0-1) | — |
| P0-3 | `PlaceOrderAsync_WhenFiltersAdjustQuantity_AppliesAdjustment` | — |
| P0-4 | `PlaceOrderAsync_WhenSellWithoutLongPosition_DoesNotOpenShort` | — |
| P1-1 | `ProcessSingleTick_WhenPositionAlreadyOpen_DoesNotDuplicate` | — |
| P1-2 | `EvaluateSignal_WhenOnlyMacd_GeneratesCrossoverSignal` | `DefaultTradingStrategyTests` |
| P1-3 | — | — (integración) |
| P2-1 | `EvaluateLeaf_Equal_UsesEpsilon` | `RuleEngineTests` |
| P2-2 | — | — |
| P2-3 | `EvaluateExitRules_TrailingStop_TriggersOnPullback` | `RuleEngineTests` |
| P2-4 | `Backtest_WithAtrSizing_UsesPositionSizer` | `BacktestEngineTests` |
| P3-1 | `ValidateOrder_WhenExpectancyNegativeButBelowThreshold_Skips` | `RiskManagerTests` |
| P3-2 | — | — (requiere test de integración) |
