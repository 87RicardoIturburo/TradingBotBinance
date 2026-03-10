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

    /// <summary>
    /// Obtiene velas (klines) históricas para backtesting.
    /// Cada elemento contiene timestamp y precio de cierre.
    /// </summary>
    Task<Result<IReadOnlyList<Kline>, DomainError>> GetKlinesAsync(
        Symbol symbol,
        DateTimeOffset from,
        DateTimeOffset to,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Obtiene los pares de trading disponibles en Binance, filtrados por quote asset.
    /// </summary>
    Task<Result<IReadOnlyList<TradingSymbolInfo>, DomainError>> GetTradingSymbolsAsync(
        string quoteAsset = "USDT",
        CancellationToken cancellationToken = default);
}

/// <summary>Vela histórica de Binance para backtesting.</summary>
public sealed record Kline(
    DateTimeOffset OpenTime,
    decimal Open,
    decimal High,
    decimal Low,
    decimal Close,
    decimal Volume);

/// <summary>Par de trading disponible en Binance.</summary>
public sealed record TradingSymbolInfo(
    string Symbol,
    string BaseAsset,
    string QuoteAsset);
