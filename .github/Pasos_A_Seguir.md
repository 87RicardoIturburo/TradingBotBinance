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

## 🔴 BUGS CRÍTICOS — Bloquean producción

> Estos bugs fueron detectados en una auditoría del código. Cada uno debe resolverse antes de operar con dinero real.

### BUG-1: Trailing stop no persiste peak prices ✅ CORREGIDO

**Archivo**: `StrategyEngine.ProcessSingleTickAsync` (línea ~894)

**Problema**: `position.UpdatePrice(tick.LastPrice)` actualiza `HighestPriceSinceEntry` / `LowestPriceSinceEntry` en memoria, pero el scope se destruye sin llamar `SaveChangesAsync`. En el siguiente tick, la posición se carga de nuevo desde la DB con los valores originales.

**Impacto**: El trailing stop **nunca rastrea el máximo real**. Si BTC sube de 60k→70k, el peak sigue en 60k. El trailing stop es inútil.

**Nota**: La migración `AddPositionPeakPriceTracking` ya existe, las columnas están en la DB. Solo falta persistir los cambios.

**Solución**: Después del `foreach (var position in openPositions)`, guardar las posiciones que cambiaron con `positionRepo.UpdateAsync` + `unitOfWork.SaveChangesAsync`.

---

### BUG-2: Warm-up ignora el timeframe de la estrategia ✅ CORREGIDO

**Archivo**: `StrategyEngine.WarmUpIndicatorsAsync` (línea ~269)

**Problema**: Siempre usa `AddMinutes(-count)` para calcular el rango histórico, sin importar el timeframe de la estrategia.

```csharp
var from = DateTimeOffset.UtcNow.AddMinutes(-count); // ← siempre minutos
```

Para una estrategia 4H con RSI(14), necesita `14 × 4h = 56 horas` de datos, pero pide solo `24 minutos`. Los indicadores arrancan con datos insuficientes.

**Solución**: Multiplicar `count` por la duración en minutos del timeframe:
```csharp
var intervalMinutes = GetIntervalMinutes(config.Timeframe);
var from = DateTimeOffset.UtcNow.AddMinutes(-(count * intervalMinutes));
```

---

### BUG-3: CheckAccountDrawdownAsync nunca se invoca ✅ CORREGIDO

**Archivos**: `RiskManager.CheckAccountDrawdownAsync`, `StrategyEngine`

**Problema**: `CheckAccountDrawdownAsync` está implementado en `RiskManager` (Paso 22 lo marca ✅), pero **ningún componente lo llama**. El kill switch de drawdown de cuenta (`MaxAccountDrawdownPercent`) es código muerto.

**Impacto**: Si la cuenta pierde 50% en un día, el sistema sigue operando sin límite.

**Solución**: Llamar `CheckAccountDrawdownAsync` periódicamente desde el StrategyEngine (ej: cada N ticks o en un timer) y disparar `_circuitBreaker.Trip()` si se activa.

---

### BUG-4: Señales Sell pasan filtro de duplicados sin posición Long (Spot) ✅ CORREGIDO

**Archivo**: `StrategyEngine.ProcessSingleKlineAsync` (línea ~633)

**Problema**: La verificación anti-duplicado busca posiciones del **mismo lado** que la señal:
```csharp
if (existingPositions.Any(p => p.Symbol == signal.Symbol && p.Side == signal.Direction))
    return;
```
Para una señal Sell, busca posiciones Sell (que nunca existen en Spot). Toda señal Sell pasa. Si no hay posición Long, se envía una orden Sell inútil:
- **Paper**: `OrderSyncHandler` loguea warning, la orden queda Filled con datos inconsistentes
- **Live**: Binance la rechaza si no hay activo (gasto de rate limit + error)

**Nota**: El Paso E Fix 4 corrigió el cierre de posiciones en `OrderSyncHandler`, pero no la lógica de ENTRADA en StrategyEngine.

**Solución**: Para Sell → verificar que existe una posición **Buy** abierta para cerrar:
```csharp
if (signal.Direction == OrderSide.Buy)
{
    if (existingPositions.Any(p => p.Symbol == signal.Symbol && p.Side == OrderSide.Buy))
        return; // Ya hay Long abierto
}
else // Sell
{
    if (!existingPositions.Any(p => p.Symbol == signal.Symbol && p.Side == OrderSide.Buy))
        return; // No hay Long que cerrar → no operar
}
```

---

### BUG-5: GlobalRiskSettings se parsea sin InvariantCulture ✅ CORREGIDO

**Archivo**: `ApplicationServiceExtensions.cs` (líneas 41-52)

**Problema**: Los `decimal.TryParse` de `GlobalRiskSettings` no usan `CultureInfo.InvariantCulture`, a diferencia de `TradingFeeConfig` (líneas 60-71) que sí lo hace. En un servidor es-ES, `"100.5"` se interpreta como `1005`.

**Impacto**: Límites de riesgo globales podrían ser 10x mayores de lo configurado.

**Solución**: Agregar `NumberStyles.Number, CultureInfo.InvariantCulture` a cada `decimal.TryParse` de GlobalRiskSettings.

---

### BUG-6: Balance check falla silenciosamente en modo Live ✅ CORREGIDO

**Archivo**: `RiskManager.ValidateOrderAsync` (líneas 86-92)

**Problema**: Si el check de balance falla (ej: Binance REST caído), la orden se permite sin verificar saldo:
```csharp
_logger.LogWarning("...Continuando sin validación de balance.");
```

**Impacto**: En producción con dinero real, órdenes podrían ejecutarse sin verificar si hay saldo suficiente.

**Solución**: En modo Live/Testnet, si no se puede verificar balance, **bloquear** la orden. En PaperTrading, permitir (no tiene balance real).

---

### BUG-7: InvalidateCacheAsync no limpia balances por asset ✅ CORREGIDO

**Archivo**: `BinanceAccountService.InvalidateCacheAsync`

**Problema**: Solo elimina `account:snapshot` pero no las claves `account:balance:USDT`, `account:balance:BTC`, etc. Después de una orden, el balance por asset queda stale hasta 5 segundos. Dos órdenes rápidas podrían pasar con el mismo balance obsoleto.

**Nota**: El `OrderExecutionLock` serializa por quote asset, lo que mitiga parcialmente esto para Market orders. Pero no cubre todos los casos.

**Solución**: En `InvalidateCacheAsync`, también eliminar las claves de balance por asset (o usar un patrón wildcard con Redis `DEL`).

---

## 🟡 ISSUES DE DISEÑO — Mejoran robustez

> No bloquean producción pero reducen la calidad de simulación o agregan riesgo operativo.

### DESIGN-1: Double validation de exchange filters

**Archivos**: `OrderService.ValidateExchangeFiltersAsync` + `BinanceSpotOrderExecutor.PlaceOrderAsync`

**Problema**: Ambos obtienen filtros del exchange y ajustan cantidad/precio independientemente. Genera una llamada REST/Redis redundante y un doble `Math.Floor` en la cantidad.

**Solución**: Eliminar la validación en `BinanceSpotOrderExecutor` y confiar en que `OrderService` ya ajustó los valores. El executor solo envía a Binance.

---

### DESIGN-2: Paper Trading Limit orders se llenan inmediatamente

**Archivo**: `OrderService.SimulatePaperTradeAsync`

**Problema**: Las Limit orders se llenan al instante al `LimitPrice`. En realidad pueden no ejecutarse nunca o parcialmente. Esto sobreestima la rentabilidad en paper/backtest.

**Solución aceptable para MVP**: Documentar la limitación. Para producción: simular con probabilidad basada en distancia del precio al limit.

---

### DESIGN-3: EMA de confirmación HTF hardcoded a período 20

**Archivo**: `DefaultTradingStrategy.InitializeAsync` (línea ~62)

**Problema**: `new EmaIndicator(20)` — el período es fijo, no configurable por estrategia.

**Solución**: Agregar `ConfirmationEmaPeriod` a `RiskConfig` o como parámetro de la estrategia.

---

### DESIGN-4: Signal cooldown hardcoded a 1 minuto

**Archivo**: `DefaultTradingStrategy.SignalCooldown`

**Problema**: `TimeSpan.FromMinutes(1)` no se adapta al timeframe. Para 1D es irrelevante, para scalping 1min puede suprimir señales legítimas.

**Solución**: Calcular cooldown como porcentaje del intervalo de vela (ej: 50% del timeframe).

---

### DESIGN-5: Spot pretende soportar Short en múltiples capas

**Archivos**: `PortfolioRiskManager`, `Position`, `RuleEngine`

**Problema**: Validación de exposición Short, PnL de posiciones Sell, trailing stop para shorts — todo dead code en Binance Spot. Agrega complejidad sin valor funcional.

**Acción**: No eliminar (sirve si se agrega Margin/Futures), pero documentar que solo Long está activo en Spot.

---

## 🟢 MEJORAS PARA PRODUCCIÓN — Post-MVP

> Implementar después de resolver todos los bugs críticos y validar en Testnet.

| ID | Mejora | Descripción |
|----|--------|-------------|
| IMP-1 | Reconciliación Binance ↔ DB | Verificar periódicamente que órdenes/posiciones locales coinciden con Binance |
| IMP-2 | Alertas externas | Telegram/webhook para eventos críticos (no solo SignalR al frontend) |
| IMP-3 | Caché de posiciones abiertas | `GetOpenByStrategyIdAsync` se llama en cada tick → ~86k queries/día por estrategia |
| IMP-4 | Motivo de cierre en Position | Saber si cerró por SL, TP, trailing stop o regla de salida (para análisis de performance) |
| IMP-5 | Walk-forward analysis | Dividir klines en 70% train / 30% test en el optimizador |
| IMP-6 | Redis fallback a memoria | Si Redis cae, usar `IMemoryCache` como fallback |
| IMP-7 | API Key fuera de appsettings | `appsettings.json` línea 10 tiene API key — mover a User Secrets |

---

## 🏁 ETAPA 7 — Activación Live

> **Requisito**: todos los BUG-1 a BUG-7 resueltos y validados con tests.

### Checklist pre-live

- [x] Bugs BUG-1 a BUG-7 corregidos (376/376 tests passing)
- [ ] Testnet operando estable ≥ 2 semanas sin errores críticos
- [ ] Sharpe Ratio walk-forward > 1.0 en al menos 2 estrategias
- [ ] Max drawdown en Testnet < 15%
- [ ] Kill switch global probado (manual + automático via drawdown)
- [ ] Backup de BD verificado
- [ ] API Key de autenticación configurada (`TRADINGBOT_API_KEY`)
- [ ] `appsettings.json` sin secrets hardcoded

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