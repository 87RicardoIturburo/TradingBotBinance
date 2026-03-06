using TradingBot.Core.Common;
using TradingBot.Core.Events;
using TradingBot.Core.ValueObjects;

namespace TradingBot.Core.Interfaces.Services;

/// <summary>
/// Servicio de datos de mercado en tiempo real.
/// Mantiene la conexión WebSocket con Binance y distribuye ticks.
/// La implementación en Infrastructure gestiona la reconexión con backoff exponencial.
/// </summary>
public interface IMarketDataService
{
    /// <summary>Indica si la conexión WebSocket está activa.</summary>
    bool IsConnected { get; }

    /// <summary>Suscribe al stream de precios de un símbolo.</summary>
    Task SubscribeAsync(Symbol symbol, CancellationToken cancellationToken = default);

    /// <summary>Cancela la suscripción al stream de un símbolo.</summary>
    Task UnsubscribeAsync(Symbol symbol, CancellationToken cancellationToken = default);

    /// <summary>
    /// Devuelve un stream asíncrono de ticks para el símbolo indicado.
    /// El stream se completa cuando se cancela <paramref name="cancellationToken"/>.
    /// </summary>
    IAsyncEnumerable<MarketTickReceivedEvent> GetTickStreamAsync(
        Symbol symbol,
        CancellationToken cancellationToken = default);

    /// <summary>Obtiene el precio actual vía REST (snapshot puntual).</summary>
    Task<Result<Price, DomainError>> GetCurrentPriceAsync(
        Symbol symbol,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Obtiene precios históricos de cierre para inicializar indicadores técnicos.
    /// </summary>
    Task<Result<IReadOnlyList<decimal>, DomainError>> GetHistoricalClosesAsync(
        Symbol symbol,
        int    count,
        CancellationToken cancellationToken = default);
}
