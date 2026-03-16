using System.Collections.Concurrent;
using TradingBot.Core.Interfaces.Services;

namespace TradingBot.Application.Services;

/// <summary>
/// Implementación con <see cref="SemaphoreSlim"/> por quote asset.
/// Garantiza que validate + place ocurran de forma atómica por asset.
/// </summary>
internal sealed class OrderExecutionLock : IOrderExecutionLock
{
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(10);
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _locks = new(StringComparer.OrdinalIgnoreCase);

    public async Task<IDisposable> AcquireAsync(
        string quoteAsset,
        CancellationToken cancellationToken,
        TimeSpan? timeout = null)
    {
        ArgumentNullException.ThrowIfNull(quoteAsset);

        var semaphore = _locks.GetOrAdd(quoteAsset, _ => new SemaphoreSlim(1, 1));
        var effectiveTimeout = timeout ?? DefaultTimeout;

        var acquired = await semaphore.WaitAsync(effectiveTimeout, cancellationToken);
        if (!acquired)
            throw new TimeoutException(
                $"No se pudo adquirir el lock de ejecución para '{quoteAsset}' en {effectiveTimeout.TotalSeconds}s.");

        return new LockReleaser(semaphore);
    }

    private sealed class LockReleaser(SemaphoreSlim semaphore) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.CompareExchange(ref _disposed, 1, 0) == 0)
                semaphore.Release();
        }
    }
}
