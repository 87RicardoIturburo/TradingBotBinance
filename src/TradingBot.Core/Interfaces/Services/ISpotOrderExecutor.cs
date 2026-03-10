using TradingBot.Core.Common;
using TradingBot.Core.Entities;

namespace TradingBot.Core.Interfaces.Services;

/// <summary>
/// Ejecuta órdenes Spot en el exchange. La implementación concreta
/// (<c>BinanceSpotOrderExecutor</c>) vive en Infrastructure.
/// <para>
/// Este contrato permite que <see cref="IOrderService"/> delegue la ejecución
/// real sin depender directamente de Binance.Net.
/// </para>
/// </summary>
public interface ISpotOrderExecutor
{
    /// <summary>
    /// Envía una orden al exchange y devuelve el ID asignado por el exchange.
    /// </summary>
    Task<Result<SpotOrderResult, DomainError>> PlaceOrderAsync(
        Order order,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Consulta el estado actual de una orden en el exchange.
    /// </summary>
    Task<Result<SpotOrderStatus, DomainError>> GetOrderStatusAsync(
        string symbol,
        string exchangeOrderId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Solicita la cancelación de una orden abierta en el exchange.
    /// </summary>
    Task<Result<bool, DomainError>> CancelOrderAsync(
        string symbol,
        string exchangeOrderId,
        CancellationToken cancellationToken = default);
}

/// <summary>Resultado de colocar una orden en el exchange.</summary>
public sealed record SpotOrderResult(
    string  ExchangeOrderId,
    decimal ExecutedQuantity,
    decimal ExecutedPrice,
    string  Status);

/// <summary>Estado de una orden consultada al exchange.</summary>
public sealed record SpotOrderStatus(
    string  ExchangeOrderId,
    decimal ExecutedQuantity,
    decimal ExecutedPrice,
    string  Status,
    bool    IsCompletelyFilled,
    bool    IsCancelled);
