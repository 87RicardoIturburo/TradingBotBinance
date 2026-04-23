# GitHub Copilot – Instrucciones del Workspace: TradingBot

## 🧠 Contexto del Proyecto

Este proyecto es un **bot de trading autónomo para Binance** con frontend y backend. Opera en tiempo real usando WebSocket y REST API de Binance, ejecutando estrategias y reglas configuradas por el usuario, con capacidad de hot-reload en tiempo de ejecución. Tú eres un experto en BINANCE, trading y desarrollo de software. 

---

## 🏗️ Stack Tecnológico

| Capa               | Tecnología                           |
|--------------------|--------------------------------------|
| Backend            | C# / .NET 10 — Clean Architecture    |
| Frontend           | Blazor WebAssembly                   |
| Tiempo real        | SignalR (backend ↔ frontend)         |
| Binance SDK        | Binance.Net (NuGet)                  |
| Base de datos      | PostgreSQL (EF Core)                 |
| Caché / Estado     | Redis (StackExchange.Redis)          |
| Mensajería interna | MediatR + System.Threading.Channels  |
| Logging            | Serilog                              |
| Testing            | xUnit + NSubstitute + FluentAssertions |

---

## 📁 Estructura del Proyecto
```
TradingBot/
├── .github/
│   └── copilot-instructions.md
├── docs/
│   └── PROJECT.md
├── src/
│   ├── TradingBot.API/              # Web API + SignalR Hubs + Middleware
│   ├── TradingBot.Core/             # Dominio puro: entidades, interfaces, enums
│   ├── TradingBot.Application/      # Casos de uso, estrategias, reglas, handlers
│   │   ├── Strategies/
│   │   ├── Rules/
│   │   ├── RiskManagement/
│   │   └── Commands|Queries/        # CQRS con MediatR
│   ├── TradingBot.Infrastructure/   # Binance.Net, EF Core, Redis, Serilog
│   └── TradingBot.Frontend/         # Blazor WebAssembly
└── tests/
    ├── TradingBot.Core.Tests/
    ├── TradingBot.Application.Tests/
    └── TradingBot.Integration.Tests/
```

---

## ✅ Convenciones de Código

### General
- Usar **C# 14** con nullable reference types habilitado (`<Nullable>enable</Nullable>`)
- Preferir `record` para DTOs y value objects inmutables
- Preferir `sealed` en clases que no se hereden
- Usar `IOptions<T>` para configuración, nunca `IConfiguration` directamente en servicios
- Métodos asíncronos siempre con sufijo `Async` y `CancellationToken` como último parámetro
- No usar excepciones para flujo de control; usar `Result<T, TError>` o `OneOf`

### Nomenclatura
- Interfaces: `IMarketDataService`, `IStrategyEngine`
- Handlers MediatR: `PlaceOrderCommandHandler`, `GetStrategiesQueryHandler`
- DTOs de respuesta: `MarketTickDto`, `OrderResultDto`
- Eventos de dominio: `OrderPlacedEvent`, `StrategyUpdatedEvent`

### Seguridad — CRÍTICO
- **Las API Keys de Binance NUNCA deben llegar al frontend**
- Cifrar API Keys en base de datos con `IDataProtectionProvider`
- Validar siempre los parámetros de estrategias antes de aplicarlos
- Nunca loguear valores de API Keys o secrets

### Patrones a seguir
- **Strategy Pattern** para implementaciones de estrategias de trading
- **Observer / Event-Driven** para propagación de ticks de mercado
- **CQRS** con MediatR para operaciones de lectura y escritura
- **Repository Pattern** con EF Core para persistencia
- **Circuit Breaker** (Polly) para llamadas a Binance API
- **Usar patrones de diseño SOLID** en toda la arquitectura 

---

### Cambios en el código
- [x] **Al finalizar de realizar cambios en el código, realizar las siguientes acciones:**
    - Compilar y verificar que no hay errores de compilación
    - En caso de errores de compilación, corregirlos antes de continuar
    - Solo si es necesario realizar pruebas unitarias para validar que los cambios no rompen funcionalidades existentes
- **Cuando se agreguen nuevas propiedades a entidades de dominio que se persisten en la base de datos (EF Core + PostgreSQL), siempre crear una migración de EF Core para mantener el esquema sincronizado. Ejecutar:** `dotnet ef migrations add <NombreMigración> --project src\TradingBot.Infrastructure --startup-project src\TradingBot.API`

### Integridad de planes y seguimiento de progreso — CRÍTICO
- **Nunca marcar un paso como ✅ si no está 100% implementado, integrado y verificado en el código.** "Implementado" significa que el artefacto (clase, método, integración) existe Y se ejecuta en el flujo real — no solo que el archivo fue creado.
- Si un paso se implementó parcialmente → marcarlo como ⚠️ con descripción precisa de lo que falta.
- Si un paso no se implementó → marcarlo como ❌.
- Si un paso creó archivos o clases que no se integran en el flujo de ejecución real (código muerto) → **NO marcarlo como ✅**. Marcarlo como ⚠️ indicando "código creado pero no integrado".
- **Antes de marcar un plan como completado**, verificar mediante búsqueda en el código (`Select-String`, `find_symbol`, `code_search`) que cada artefacto creado se referencia y usa realmente en el runtime — no solo que existe como archivo.
- El porcentaje de progreso del plan debe reflejar solo los pasos realmente completados al 100%, no los intentados o parciales.
- Si durante la ejecución se descubre que un paso previo marcado como ✅ tiene deuda técnica o quedó incompleto, **corregir inmediatamente su estado** en el plan antes de continuar.

## ⚡ Reglas para el Motor de Trading

- Todo indicador técnico (RSI, MACD, EMA, etc.) debe implementar `ITechnicalIndicator`
- Toda estrategia debe implementar `ITradingStrategy` y ser registrada en DI
- Las reglas configurables deben ser deserializables desde JSON en tiempo de ejecución
- El `RiskManager` debe validar SIEMPRE antes de que `OrderManager` ejecute una orden
- Modo **Paper Trading** debe estar disponible sin modificar la lógica de estrategias

---
##  📊 Manejo de Datos y Formatos
- Usar InvariantCulture para evitar errores de locale en sistemas es-ES:
``` En español "." es separador de miles, por lo que "0.001" se parsearía como 1.0 ```
- Ejemplo de parseo correcto:
```csharp 
    services.Configure<TradingFeeConfig>(opts =>
            {
                if (decimal.TryParse(feeSection[nameof(TradingFeeConfig.MakerFeePercent)],
                        NumberStyles.Number, CultureInfo.InvariantCulture, out var mf))
                    opts.MakerFeePercent = mf;
                if (decimal.TryParse(feeSection[nameof(TradingFeeConfig.TakerFeePercent)],
                        NumberStyles.Number, CultureInfo.InvariantCulture, out var tf))
                    opts.TakerFeePercent = tf;
                if (bool.TryParse(feeSection[nameof(TradingFeeConfig.UseBnbDiscount)], out var bnb))
                    opts.UseBnbDiscount = bnb;
                if (decimal.TryParse(feeSection[nameof(TradingFeeConfig.SlippagePercent)],
                        NumberStyles.Number, CultureInfo.InvariantCulture, out var sp))
                    opts.SlippagePercent = sp;
            });
  ```          
---

## 🧪 Testing

- Cada estrategia debe tener tests unitarios con datos históricos simulados
- Los handlers de MediatR deben testearse de forma aislada con mocks de repositorios
- Usar `FluentAssertions` para aserciones legibles
- Usar `NSubstitute` para mocking, nunca `Moq`
- Nombrar tests: `[Método]_[Escenario]_[ResultadoEsperado]`

---

## 📡 Binance API

- Usar `Binance.Net` para todas las interacciones (REST y WebSocket)
- WebSocket: reconexión automática con backoff exponencial
- Respetar rate limits de Binance; usar el middleware de throttling incluido
- Ambiente de desarrollo: usar **Binance Demo** (`demo.binance.com`) con `UseDemo: true`
- Variables de entorno para keys: `BINANCE_API_KEY`, `BINANCE_API_SECRET`
- Variables de entorno para entorno: `BINANCE_USE_TESTNET`, `BINANCE_USE_DEMO`
- Entornos disponibles: `BinanceEnvironment.Demo` (demo.binance.com), `BinanceEnvironment.Testnet` (testnet.binance.vision), producción (por defecto)

---

## Documentación oficial de Binance API

- Documentación general: https://developers.binance.com/docs/
- Binance Developer Portal: https://developers.binance.com/en
- Spot Trading API: https://developers.binance.com/docs/binance-spot-api-docs
- Margin Trading API: https://developers.binance.com/docs/margin_trading/Introduction
- Derivatives Trading: https://developers.binance.com/docs/derivatives/change-log
- Alpha API: https://developers.binance.com/docs/alpha/change-log
- Algo Trading: https://developers.binance.com/docs/algo/change-log
- Wallet: https://developers.binance.com/docs/wallet/change-log
- Convert: https://developers.binance.com/docs/convert/change-log
- Binance Open API: https://developers.binance.com/docs/binance-open-api/apis
- Institutional Loan: https://developers.binance.com/docs/institutional_loan/change-log

---

## ✅ Lo que SI hacer
- Actualizar `.github/PROJECT.md` con cualquier cambio relevante en la arquitectura o funcionalidades
- Actualizar `.github/Pasos_A_Seguir.md` a medida que se cumplen hitos importantes del proyecto
- Responde siempre en español
- Programa en ingles, pero documenta en español
- Evitar poner comentarios, colocar solo cuando sea muy necesario. ejemplo:
 ```csharp
 /// <summary>
/// Motor de estrategias. Orquesta el flujo completo como <see cref="BackgroundService"/>:
/// <c>MarketTick → ITradingStrategy → IRuleEngine → IRiskManager → IOrderService</c>.
/// <para>
/// Cada estrategia activa se ejecuta en su propio loop asíncrono consumiendo
/// el <see cref="IAsyncEnumerable{T}"/> de ticks del <see cref="IMarketDataService"/>.
/// </para>
/// </summary>
 ```

---

## 🚫 Lo que NO hacer

- No usar `Thread.Sleep` — siempre `await Task.Delay`
- No usar `async void` — excepto en event handlers de Blazor
- No hacer llamadas a Binance REST directamente desde controladores
- No modificar estrategias activas sin pasar por `IStrategyConfigService`
- No commitear archivos `appsettings.Development.json` con keys reales
- No coloques comentarios de código, ejemplo `// CRIT-NEW-4 fix: ReloadConfigAsync ejecuta RebuildIndicators que limpia` 
- **No marcar pasos de un plan como ✅ (completados) sin verificar que el código existe, compila, se integra en el flujo real y no es código muerto. No marcar un plan como 100% completado si tiene pasos parciales (⚠️) o fallidos (❌).**
