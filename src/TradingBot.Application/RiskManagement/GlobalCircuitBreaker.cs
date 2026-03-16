using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using TradingBot.Core.Interfaces.Services;

namespace TradingBot.Application.RiskManagement;

/// <summary>
/// Circuit breaker global. Se auto-dispara si se acumulan demasiados errores
/// del exchange en una ventana de tiempo. Thread-safe para uso concurrente.
/// </summary>
internal sealed class GlobalCircuitBreaker : IGlobalCircuitBreaker
{
    private readonly ILogger<GlobalCircuitBreaker> _logger;
    private readonly ConcurrentQueue<DateTimeOffset> _recentErrors = new();
    private readonly ConcurrentQueue<DateTimeOffset> _recentRateLimits = new();
    private readonly object _tripLock = new();

    /// <summary>Máximo de errores REST en la ventana antes de auto-trip.</summary>
    private const int MaxRestErrors = 10;
    private static readonly TimeSpan RestErrorWindow = TimeSpan.FromMinutes(5);

    /// <summary>Máximo de rate limits en la ventana antes de auto-trip.</summary>
    private const int MaxRateLimits = 3;
    private static readonly TimeSpan RateLimitWindow = TimeSpan.FromMinutes(1);

    public bool IsOpen { get; private set; }
    public string? TripReason { get; private set; }
    public DateTimeOffset? TrippedAt { get; private set; }

    public GlobalCircuitBreaker(ILogger<GlobalCircuitBreaker> logger)
    {
        _logger = logger;
    }

    public void Trip(string reason)
    {
        lock (_tripLock)
        {
            if (IsOpen) return;

            IsOpen = true;
            TripReason = reason;
            TrippedAt = DateTimeOffset.UtcNow;

            _logger.LogCritical(
                "🚨 CIRCUIT BREAKER ABIERTO: {Reason}. Todo el trading detenido.",
                reason);
        }
    }

    public void Reset()
    {
        lock (_tripLock)
        {
            IsOpen = false;
            TripReason = null;
            TrippedAt = null;

            // Limpiar contadores
            while (_recentErrors.TryDequeue(out _)) { }
            while (_recentRateLimits.TryDequeue(out _)) { }

            _logger.LogWarning("Circuit breaker reseteado. Trading reanudado.");
        }
    }

    public void RecordExchangeError(string source)
    {
        var now = DateTimeOffset.UtcNow;
        var isRateLimit = source.Contains("rate limit", StringComparison.OrdinalIgnoreCase)
                       || source.Contains("-1015", StringComparison.Ordinal)
                       || source.Contains("429", StringComparison.Ordinal);

        if (isRateLimit)
        {
            _recentRateLimits.Enqueue(now);
            PruneQueue(_recentRateLimits, RateLimitWindow);

            if (_recentRateLimits.Count >= MaxRateLimits)
            {
                Trip($"Rate limit excedido: {_recentRateLimits.Count} rate limits en {RateLimitWindow.TotalMinutes} min (fuente: {source})");
                return;
            }
        }

        _recentErrors.Enqueue(now);
        PruneQueue(_recentErrors, RestErrorWindow);

        if (_recentErrors.Count >= MaxRestErrors)
        {
            Trip($"Demasiados errores REST: {_recentErrors.Count} en {RestErrorWindow.TotalMinutes} min (último: {source})");
        }
    }

    public void RecordExchangeSuccess()
    {
        // Éxito reduce la presión: limpiamos entradas viejas
        PruneQueue(_recentErrors, RestErrorWindow);
        PruneQueue(_recentRateLimits, RateLimitWindow);
    }

    private static void PruneQueue(ConcurrentQueue<DateTimeOffset> queue, TimeSpan window)
    {
        var cutoff = DateTimeOffset.UtcNow - window;
        while (queue.TryPeek(out var oldest) && oldest < cutoff)
            queue.TryDequeue(out _);
    }
}
