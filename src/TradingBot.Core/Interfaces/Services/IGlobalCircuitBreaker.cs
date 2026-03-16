namespace TradingBot.Core.Interfaces.Services;

/// <summary>
/// Circuit breaker global que detiene todo el trading ante problemas sistémicos.
/// Cualquier servicio puede dispararlo y debe verificarlo antes de operar.
/// </summary>
public interface IGlobalCircuitBreaker
{
    /// <summary>Indica si el circuit breaker está abierto (trading detenido).</summary>
    bool IsOpen { get; }

    /// <summary>Razón del último disparo, o <c>null</c> si está cerrado.</summary>
    string? TripReason { get; }

    /// <summary>Momento en que se disparó, o <c>null</c> si está cerrado.</summary>
    DateTimeOffset? TrippedAt { get; }

    /// <summary>Dispara manualmente el circuit breaker con la razón indicada.</summary>
    void Trip(string reason);

    /// <summary>Resetea el circuit breaker para reanudar el trading.</summary>
    void Reset();

    /// <summary>Registra un error del exchange para auto-trip basado en umbrales.</summary>
    void RecordExchangeError(string source);

    /// <summary>Registra una operación exitosa del exchange para reducir el contador de errores.</summary>
    void RecordExchangeSuccess();
}
