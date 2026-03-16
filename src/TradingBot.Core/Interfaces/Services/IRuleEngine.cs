using TradingBot.Core.Common;
using TradingBot.Core.Entities;
using TradingBot.Core.Events;
using TradingBot.Core.ValueObjects;

namespace TradingBot.Core.Interfaces.Services;

/// <summary>
/// Motor de reglas. Evalúa las <see cref="TradingRule"/> de una estrategia
/// frente a una señal y decide si se debe ejecutar una orden.
/// Las reglas se cargan desde JSON en tiempo de ejecución (hot-reload).
/// </summary>
public interface IRuleEngine
{
    /// <summary>
    /// Evalúa las reglas de la estrategia ante la señal recibida.
    /// Devuelve la orden a ejecutar si alguna regla de entrada/salida se activa,
    /// o <c>null</c> si ninguna regla aplica en este tick.
    /// </summary>
    Task<Result<Order?, DomainError>> EvaluateAsync(
        TradingStrategy     strategy,
        SignalGeneratedEvent signal,
        CancellationToken   cancellationToken = default);

    /// <summary>
    /// Evalúa las reglas de stop-loss y take-profit para posiciones abiertas.
    /// Se llama en cada tick para posiciones activas de la estrategia.
    /// </summary>
    /// <param name="atrValue">Valor actual del ATR. Si no es <c>null</c> y <c>UseAtrSizing</c> está habilitado, se usa para stop-loss dinámico.</param>
    /// <param name="indicatorSnapshot">Snapshot de valores actuales de indicadores (ej: "RSI(14)=28.50 | EMA(12)=...").
    /// Si se provee, las condiciones de las reglas de salida pueden evaluarse contra indicadores reales.</param>
    Task<Result<Order?, DomainError>> EvaluateExitRulesAsync(
        TradingStrategy strategy,
        Position        position,
        Price           currentPrice,
        CancellationToken cancellationToken = default,
        decimal?        atrValue = null,
        string?         indicatorSnapshot = null);
}
