using TradingBot.Core.Common;
using TradingBot.Core.ValueObjects;

namespace TradingBot.Core.Interfaces.Services;

/// <summary>
/// Obtiene y cachea los filtros del exchange por símbolo.
/// Permite validar y ajustar cantidad/precio antes de enviar una orden a Binance.
/// </summary>
public interface IExchangeInfoService
{
    /// <summary>
    /// Devuelve los filtros activos para el símbolo indicado.
    /// Los resultados se cachean en Redis durante 1 hora.
    /// </summary>
    Task<Result<ExchangeSymbolFilters, DomainError>> GetFiltersAsync(
        string            symbol,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Invalida la entrada de caché para un símbolo (útil si Binance actualiza sus filtros).
    /// </summary>
    Task InvalidateCacheAsync(
        string            symbol,
        CancellationToken cancellationToken = default);
}
