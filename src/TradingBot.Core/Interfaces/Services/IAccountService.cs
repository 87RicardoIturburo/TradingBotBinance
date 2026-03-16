using TradingBot.Core.Common;
using TradingBot.Core.ValueObjects;

namespace TradingBot.Core.Interfaces.Services;

/// <summary>
/// Consulta el balance de la cuenta Binance.
/// Los datos se cachean brevemente (5 s) para reducir llamadas REST.
/// Las API Keys NUNCA se exponen al frontend — este servicio sólo vive en el backend.
/// </summary>
public interface IAccountService
{
    /// <summary>
    /// Devuelve el saldo libre (disponible para operar) de un asset concreto (p. ej. "USDT", "BTC").
    /// </summary>
    Task<Result<decimal, DomainError>> GetAvailableBalanceAsync(
        string            asset,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Snapshot de todos los assets con saldo total > 0.
    /// </summary>
    Task<Result<IReadOnlyList<AccountBalance>, DomainError>> GetAccountSnapshotAsync(
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Invalida la caché de balance para forzar una consulta fresca en la siguiente llamada.
    /// Se invoca automáticamente tras ejecutar o cancelar una orden.
    /// </summary>
    Task InvalidateCacheAsync(CancellationToken cancellationToken = default);
}
