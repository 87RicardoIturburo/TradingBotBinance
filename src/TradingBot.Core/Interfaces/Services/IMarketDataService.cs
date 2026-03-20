using TradingBot.Core.Common;
using TradingBot.Core.Enums;
using TradingBot.Core.Events;
using TradingBot.Core.ValueObjects;

namespace TradingBot.Core.Interfaces.Services;

/// <summary>
/// Servicio de datos de mercado en tiempo real.
/// Mantiene la conexión WebSocket con Binance y distribuye ticks y velas.
/// La implementación en Infrastructure gestiona la reconexión con backoff exponencial.
/// </summary>
public interface IMarketDataService
{
    /// <summary>Indica si la conexión WebSocket está activa.</summary>
    bool IsConnected { get; }

    /// <summary>Suscribe al stream de precios (ticker) de un símbolo.</summary>
    Task SubscribeAsync(Symbol symbol, CancellationToken cancellationToken = default);

    /// <summary>Cancela la suscripción al stream de un símbolo.</summary>
    Task UnsubscribeAsync(Symbol symbol, CancellationToken cancellationToken = default);

    /// <summary>
    /// Suscribe al stream de velas (klines) de un símbolo con el intervalo indicado.
    /// Solo emite eventos cuando la vela se cierra (<c>Final == true</c>).
    /// </summary>
    Task SubscribeKlinesAsync(Symbol symbol, CandleInterval interval, CancellationToken cancellationToken = default);

    /// <summary>
    /// Devuelve un stream asíncrono de ticks para el símbolo indicado.
    /// El stream se completa cuando se cancela <paramref name="cancellationToken"/>.
    /// </summary>
    IAsyncEnumerable<MarketTickReceivedEvent> GetTickStreamAsync(
        Symbol symbol,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Devuelve un stream asíncrono de velas cerradas para el símbolo e intervalo indicados.
    /// El stream se completa cuando se cancela <paramref name="cancellationToken"/>.
    /// </summary>
    IAsyncEnumerable<KlineClosedEvent> GetKlineStreamAsync(
        Symbol symbol,
        CandleInterval interval,
        CancellationToken cancellationToken = default);

    /// <summary>Obtiene el precio actual vía REST (snapshot puntual).</summary>
    Task<Result<Price, DomainError>> GetCurrentPriceAsync(
        Symbol symbol,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Devuelve el último bid y ask recibidos por WebSocket para el símbolo indicado.
    /// Retorna <c>null</c> si no hay datos disponibles.
    /// </summary>
    (Price Bid, Price Ask)? GetLastBidAsk(Symbol symbol);

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
        CandleInterval interval = CandleInterval.OneMinute, 
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Obtiene los pares de trading disponibles en Binance, filtrados por quote asset.
    /// </summary>
    Task<Result<IReadOnlyList<TradingSymbolInfo>, DomainError>> GetTradingSymbolsAsync(
        string quoteAsset = "USDT",
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Obtiene los tickers de 24h de todos los símbolos activos en Binance.
    /// Usado por el Market Scanner para calcular el Tradability Score.
    /// </summary>
    Task<Result<IReadOnlyList<Ticker24h>, DomainError>> Get24hTickersAsync(
        CancellationToken cancellationToken = default);
}

/// <summary>Ticker de 24 horas de Binance con métricas de volumen y precio.</summary>
public sealed record Ticker24h(
    string Symbol,
    decimal LastPrice,
    decimal BidPrice,
    decimal AskPrice,
    decimal Volume24h,
    decimal QuoteVolume24h,
    decimal PriceChangePercent24h,
    decimal HighPrice24h,
    decimal LowPrice24h);

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
