using TradingBot.Core.Common;
using TradingBot.Core.Entities;
using TradingBot.Core.ValueObjects;

namespace TradingBot.Core.Interfaces.Services;

/// <summary>
/// Servicio de configuración de estrategias con soporte de hot-reload.
/// Es el único punto de entrada para crear, modificar o eliminar estrategias activas.
/// Valida el esquema antes de aplicar y publica <c>StrategyUpdatedEvent</c> al bus interno.
/// </summary>
public interface IStrategyConfigService
{
    Task<Result<TradingStrategy, DomainError>> GetByIdAsync(
        Guid id,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<TradingStrategy>> GetAllAsync(
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<TradingStrategy>> GetAllActiveAsync(
        CancellationToken cancellationToken = default);

    /// <summary>Crea y persiste una nueva estrategia en estado Inactive.</summary>
    Task<Result<TradingStrategy, DomainError>> CreateAsync(
        TradingStrategy strategy,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Actualiza la configuración de una estrategia y dispara el hot-reload
    /// si estaba activa en el momento de la llamada.
    /// </summary>
    Task<Result<TradingStrategy, DomainError>> UpdateAsync(
        TradingStrategy strategy,
        CancellationToken cancellationToken = default);

    /// <summary>Activa una estrategia. Valida que tenga reglas habilitadas.</summary>
    Task<Result<TradingStrategy, DomainError>> ActivateAsync(
        Guid id,
        CancellationToken cancellationToken = default);

    /// <summary>Desactiva una estrategia deteniendo su procesamiento de ticks.</summary>
    Task<Result<TradingStrategy, DomainError>> DeactivateAsync(
        Guid id,
        CancellationToken cancellationToken = default);

    /// <summary>Elimina una estrategia. No permitido si está activa.</summary>
    Task<Result<bool, DomainError>> DeleteAsync(
        Guid id,
        CancellationToken cancellationToken = default);
}
