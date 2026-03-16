namespace TradingBot.Core.Interfaces.Services;

/// <summary>
/// Semáforo por quote asset para serializar validación + ejecución de órdenes,
/// evitando race conditions entre estrategias que compiten por el mismo saldo.
/// </summary>
public interface IOrderExecutionLock
{
    /// <summary>
    /// Adquiere el lock para el quote asset indicado. Devuelve un <see cref="IDisposable"/>
    /// que libera el lock al hacer Dispose.
    /// </summary>
    Task<IDisposable> AcquireAsync(string quoteAsset, CancellationToken cancellationToken, TimeSpan? timeout = null);
}
