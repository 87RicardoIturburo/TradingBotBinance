# TradingBot — Plan de Trabajo

> **Contexto para Copilot.** Leer junto con `.github/copilot-instructions.md` y `.github/PROJECT.md`.

---

## 📊 Estado actual

| Métrica | Valor |
|---------|-------|
| Build | ✅ 8 proyectos compilan |
| Tests | ✅ 378+ passing — Core + Application + Integration |
| TFM | .NET 10 / C# 14 |
| Migraciones EF Core | 13 aplicadas |
| Etapas 1–7 | ✅ Completadas |
| IMP pendientes | IMP-2 (Alertas externas Telegram/webhook) |

---

## 🚨 Auditoría de producción — Bugs críticos encontrados

### CRIT-A: Doble creación de posiciones en `UserDataStreamService` — 🔴 BLOQUEANTE
**Archivo:** `src/TradingBot.Infrastructure/Binance/UserDataStreamService.cs` línea ~238  
**Problema:** El resultado de `tracked.Fill()` no se verifica antes de llamar `HandleOrderFilledAsync`. Si `OrderService` ya procesó el fill vía REST y luego llega el `executionReport` por WebSocket, `Fill()` retorna Failure (ya está Filled) pero `HandleOrderFilledAsync` se ejecuta igual → crea una **posición fantasma duplicada**.  
**Impacto:** Capital atrapado, métricas de riesgo distorsionadas, Sell solo cierra una de las dos posiciones.  
**Fix:** Verificar resultado de `Fill()` antes de llamar `HandleOrderFilledAsync`. Solo proceder si `Fill()` fue exitoso.  
**Estado:** ✅ Corregido

### CRIT-B: Warm-up insuficiente para MACD — 🔴 BLOQUEANTE
**Archivo:** `src/TradingBot.Application/Strategies/StrategyEngine.cs` línea ~261  
**Problema:** El cálculo de `maxPeriod` solo busca el parámetro `"period"` en cada indicador. MACD usa `fastPeriod`, `slowPeriod`, `signalPeriod` → el default es 14, pero necesita ≥35 (26+9). Las primeras señales MACD serán con indicador no convergido.  
**Fix:** Calcular warm-up considerando todos los parámetros de periodo según tipo de indicador, incluyendo `slowPeriod` + `signalPeriod` para MACD, `stdDev` no afecta pero sí el `period` de BB/ADX/ATR.  
**Estado:** ✅ Corregido

### CRIT-C: `MaxSpreadPercent` de `RiskConfig` ignorado — 🟠 ALTA
**Archivo:** `src/TradingBot.Application/Services/OrderService.cs` línea ~492  
**Problema:** Usa `const decimal defaultMaxSpread = 1.0m` hardcoded. `RiskConfig.MaxSpreadPercent` configurado por estrategia nunca se usa en la validación de spread.  
**Fix:** Obtener la estrategia del repositorio y usar su `RiskConfig.MaxSpreadPercent`.  
**Estado:** ✅ Corregido

### CRIT-D: Drawdown subestimado — Solo considera posiciones cerradas — 🔴 BLOQUEANTE
**Archivo:** `src/TradingBot.Application/RiskManagement/RiskManager.cs` línea ~263  
**Problema:** `CheckAccountDrawdownAsync` calcula P&L diario solo de posiciones cerradas. Posiciones abiertas con pérdidas no realizadas no se cuentan → kill switch no se activa durante caídas sostenidas.  
**Fix:** Incluir `UnrealizedPnL` de posiciones abiertas en el cálculo de drawdown.  
**Estado:** ✅ Corregido

### DES-A: Fee buffer del 5% excesivamente conservador — 🟠 ALTA
**Archivo:** `src/TradingBot.Application/RiskManagement/RiskManager.cs` línea ~28  
**Problema:** `FeeBuffer = 0.05m` (5%) cuando Binance cobra 0.1% máximo. Bloquea ~$4.76 por cada $100 de capital sin razón. En fase Alpha ($50 USDT) es significativo.  
**Fix:** Calcular dinámicamente desde `TradingFeeConfig.EffectiveTakerFee` + margen razonable (3x fee real).  
**Estado:** ✅ Corregido

### DES-B: Sell orders de cierre bloqueadas por validación Short en Spot — 🟠 ALTA
**Archivo:** `src/TradingBot.Application/RiskManagement/PortfolioRiskManager.cs` línea ~67  
**Problema:** Un Sell en Spot cierra un Long, no crea Short exposure. Pero la validación trata Sell como nueva exposición Short. Si `MaxPortfolioShortExposureUsdt > 0`, puede bloquear cierres de posiciones perdedoras.  
**Fix:** Detectar si el Sell cierra una posición Long existente y excluirlo de la validación Short.  
**Estado:** ✅ Corregido

---

## 🟡 Problemas de diseño identificados (no bloqueantes)

| ID | Problema | Archivo | Descripción |
|----|----------|---------|-------------|
| DES-C | Sin lock en WebSocket fills | `UserDataStreamService.cs:188` | `Task.Run` fire-and-forget puede causar race conditions en posición. Mitigado por el xmin concurrency token de EF Core. |
| DES-D | HighVolatility suprime TODAS las señales | `DefaultTradingStrategy.cs:351` | BB BW>8% o ATR>3% → bot paralizado. En crypto es normal. Debería ser configurable por estrategia. |
| DES-E | Backtest CloseTime hardcoded +1min | `BacktestEngine.cs:74` | Para velas 4H/1D el CloseTime es incorrecto. Afecta cooldown de señales en backtest. |
| EST-A | RSI captura cuchillos cayendo | `DefaultTradingStrategy.cs:438` | En downtrend fuerte, RSI<30 genera Buy repetidos. Mitigado parcialmente por ADX trend filter. |
| EST-B | Trailing ATR + Trailing % doble | `RuleEngine.cs:71-197` | 3 mecanismos de stop evaluados secuencialmente. Confuso pero el más estricto gana (seguro). |
| EST-C | Confirmación asimétrica | `DefaultTradingStrategy.cs:398` | 1 confirmador = 100% req. 4 confirmadores = 50% req. Más indicadores → más fácil entrar. |
| EST-D | Paper trading sin balance | `RiskManager.cs:66` | Paper puede gastar ilimitado. Resultados no representativos del capital real. |

---

## ✅ Checklist pre-live

- [x] Bugs BUG-1 a BUG-7 corregidos
- [x] CRIT-1 a CRIT-6 implementados
- [x] TRADE-1 a TRADE-4 implementados
- [x] CRIT-A a CRIT-D de auditoría corregidos
- [x] DES-A y DES-B de auditoría corregidos
- [x] 378+ tests passing
- [x] GlobalRisk configurados con valores conservadores
- [x] IMP-1 (Reconciliación) implementada
- [x] IMP-7 (Fee tracking BNB) implementada
- [ ] IMP-2 (Alertas externas Telegram/webhook)
- [ ] Testnet operando estable ≥ 2 semanas sin errores críticos
- [ ] Sharpe Ratio walk-forward > 1.0 en al menos 2 estrategias
- [ ] Max drawdown en Testnet < 15%
- [ ] Kill switch global probado (manual + automático vía drawdown)
- [ ] Backup de BD verificado
- [ ] API Key de autenticación configurada (`TRADINGBOT_API_KEY`)
- [ ] `appsettings.json` sin secrets hardcoded

### Activación por fases

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