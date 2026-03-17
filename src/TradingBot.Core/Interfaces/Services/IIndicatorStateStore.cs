using TradingBot.Core.Enums;

namespace TradingBot.Core.Interfaces.Services;

/// <summary>
/// Almacena y restaura el estado serializado de los indicadores técnicos de una estrategia.
/// Permite que los indicadores sobrevivan reinicios sin necesidad de warm-up desde cero.
/// La implementación concreta usa Redis con TTL de 24 horas.
/// </summary>
public interface IIndicatorStateStore
{
    /// <summary>
    /// Guarda el estado de todos los indicadores de una estrategia.
    /// </summary>
    /// <param name="strategyId">ID de la estrategia.</param>
    /// <param name="states">Diccionario con clave = tipo de indicador, valor = JSON serializado.</param>
    /// <param name="cancellationToken">Token de cancelación.</param>
    Task SaveAsync(
        Guid strategyId,
        IReadOnlyDictionary<IndicatorType, string> states,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Restaura el estado de los indicadores de una estrategia.
    /// </summary>
    /// <returns>Diccionario con clave = tipo de indicador, valor = JSON serializado; o <c>null</c> si no hay estado guardado.</returns>
    Task<IReadOnlyDictionary<IndicatorType, string>?> RestoreAsync(
        Guid strategyId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Elimina el estado guardado de una estrategia (e.g., al cambiar configuración de indicadores).
    /// </summary>
    Task RemoveAsync(Guid strategyId, CancellationToken cancellationToken = default);
}
