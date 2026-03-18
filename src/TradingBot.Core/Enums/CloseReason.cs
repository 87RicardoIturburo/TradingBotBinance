namespace TradingBot.Core.Enums;

/// <summary>
/// Motivo de cierre de una posición. Permite análisis post-trade y
/// métricas de rendimiento por tipo de salida.
/// </summary>
public enum CloseReason
{
    /// <summary>Precio cayó por debajo del stop-loss fijo (% del entry).</summary>
    StopLoss,

    /// <summary>Precio alcanzó el take-profit configurado.</summary>
    TakeProfit,

    /// <summary>Trailing stop activado: retroceso desde el pico histórico.</summary>
    TrailingStop,

    /// <summary>Regla de salida basada en indicadores (ej: RSI cruzó umbral).</summary>
    ExitRule,

    /// <summary>Cierre manual solicitado por el usuario o por API.</summary>
    Manual,

    /// <summary>Cierre forzado por el circuit breaker o kill switch global.</summary>
    CircuitBreaker,

    /// <summary>Posición cerrada por reconciliación con Binance.</summary>
    Reconciliation
}
