using BinanceEnums = Binance.Net.Enums;
using Binance.Net.Interfaces.Clients;
using Microsoft.Extensions.Logging;
using Polly;
using Polly.Retry;
using TradingBot.Core.Common;
using TradingBot.Core.Entities;
using TradingBot.Core.Interfaces.Services;
using CoreOrderSide = TradingBot.Core.Enums.OrderSide;
using CoreOrderType = TradingBot.Core.Enums.OrderType;

namespace TradingBot.Infrastructure.Binance;

/// <summary>
/// Ejecuta órdenes Spot en Binance vía REST API.
/// Usa Polly para reintentos con backoff exponencial + jitter.
/// Errores no retryables: -1013 (filter), -2010 (insufficient balance), -1021 (timestamp).
/// </summary>
internal sealed class BinanceSpotOrderExecutor : ISpotOrderExecutor
{
    private readonly IBinanceRestClient _restClient;
    private readonly ILogger<BinanceSpotOrderExecutor> _logger;
    private readonly ResiliencePipeline _retryPipeline;

    /// <summary>Códigos de error Binance que NO deben reintentar (fallo determinístico).</summary>
    private static readonly HashSet<int> NonRetryableErrorCodes = [
        -1013, // Filter failure (LOT_SIZE, PRICE_FILTER, MIN_NOTIONAL)
        -2010, // Insufficient balance
        -1021, // Timestamp outside recvWindow
        -1100, // Illegal characters in parameter
        -1102, // Mandatory parameter missing
        -1116, // Invalid order type
        -1117, // Invalid side
    ];

    /// <summary>Códigos de error Binance que indican mantenimiento/sobrecarga.</summary>
    private static readonly HashSet<int> MaintenanceErrorCodes = [
        -1003, // Too many requests (rate limit)
        -1015, // Too many new orders
    ];

    public BinanceSpotOrderExecutor(
        IBinanceRestClient restClient,
        ILogger<BinanceSpotOrderExecutor> logger)
    {
        _restClient          = restClient;
        _logger              = logger;

        _retryPipeline = new ResiliencePipelineBuilder()
            .AddRetry(new RetryStrategyOptions
            {
                MaxRetryAttempts = 3,
                BackoffType      = DelayBackoffType.Exponential,
                UseJitter        = true,
                Delay            = TimeSpan.FromMilliseconds(500),
                ShouldHandle = new PredicateBuilder().Handle<Exception>(ex =>
                    ex is not BinanceNonRetryableException),
                OnRetry = args =>
                {
                    logger.LogWarning(
                        "Reintentando orden Binance (intento {Attempt}): {Error}",
                        args.AttemptNumber + 1,
                        args.Outcome.Exception?.Message);
                    return default;
                }
            })
            .AddTimeout(TimeSpan.FromSeconds(15))
            .Build();
    }

    public async Task<Result<SpotOrderResult, DomainError>> PlaceOrderAsync(
        Order order,
        CancellationToken cancellationToken = default)
    {
        try
        {
            // Los filtros de exchange (LOT_SIZE, PRICE_FILTER, MIN_NOTIONAL) ya fueron
            // aplicados por OrderService.ValidateExchangeFiltersAsync. El executor solo envía.
            var adjustedQty   = order.Quantity.Value;
            var adjustedPrice = order.LimitPrice?.Value;

            var binanceSide = order.Side == CoreOrderSide.Buy
                ? BinanceEnums.OrderSide.Buy
                : BinanceEnums.OrderSide.Sell;

            var binanceType = MapOrderType(order.Type);

            var response = await _retryPipeline.ExecuteAsync(async ct =>
            {
                var callResult = await _restClient.SpotApi.Trading.PlaceOrderAsync(
                    symbol: order.Symbol.Value,
                    side: binanceSide,
                    type: binanceType,
                    quantity: adjustedQty,
                    price: adjustedPrice,
                    stopPrice: order.StopPrice?.Value,
                    timeInForce: binanceType == BinanceEnums.SpotOrderType.Market
                        ? null
                        : BinanceEnums.TimeInForce.GoodTillCanceled,
                    ct: ct);

                if (!callResult.Success)
                {
                    var errorCode = callResult.Error?.Code;
                    var errorMsg = callResult.Error?.Message ?? "Error desconocido al colocar orden en Binance.";

                    // Clasificar errores Binance
                    if (errorCode.HasValue && NonRetryableErrorCodes.Contains(errorCode.Value))
                        throw new BinanceNonRetryableException(errorCode.Value, errorMsg);

                    if (errorCode.HasValue && MaintenanceErrorCodes.Contains(errorCode.Value))
                    {
                        _logger.LogWarning(
                            "Binance sobrecargado/mantenimiento (código {Code}): {Message}",
                            errorCode.Value, errorMsg);
                    }

                    throw new InvalidOperationException(errorMsg);
                }

                return callResult;
            }, cancellationToken);

            if (!response.Success)
            {
                _logger.LogError(
                    "Binance rechazó orden {OrderId}: {Error}",
                    order.Id, response.Error?.Message);
                return Result<SpotOrderResult, DomainError>.Failure(
                    DomainError.ExternalService(response.Error?.Message ?? "Binance rechazó la orden."));
            }

            var data = response.Data;
            _logger.LogInformation(
                "Orden Binance colocada: {BinanceId} {Side} {Qty} {Symbol} Status={Status}",
                data.Id, data.Side, data.Quantity, data.Symbol, data.Status);

            return Result<SpotOrderResult, DomainError>.Success(new SpotOrderResult(
                ExchangeOrderId: data.Id.ToString(),
                ExecutedQuantity: data.QuantityFilled,
                ExecutedPrice: data.AverageFillPrice ?? data.Price,
                Status: data.Status.ToString()));
        }
        catch (BinanceNonRetryableException ex)
        {
            _logger.LogWarning(
                "Orden {OrderId} rechazada por Binance (código {Code}): {Message}",
                order.Id, ex.ErrorCode, ex.Message);
            return Result<SpotOrderResult, DomainError>.Failure(
                DomainError.ExternalService($"Binance error {ex.ErrorCode}: {ex.Message}"));
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogError(ex, "Error al colocar orden {OrderId} tras reintentos", order.Id);
            return Result<SpotOrderResult, DomainError>.Failure(
                DomainError.ExternalService(ex.Message));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error inesperado al colocar orden {OrderId}", order.Id);
            return Result<SpotOrderResult, DomainError>.Failure(
                DomainError.ExternalService($"Error al colocar orden: {ex.Message}"));
        }
    }

    public async Task<Result<SpotOrderStatus, DomainError>> GetOrderStatusAsync(
        string symbol,
        string exchangeOrderId,
        CancellationToken cancellationToken = default)
    {
        try
        {
            if (!long.TryParse(exchangeOrderId, out var binanceOrderId))
                return Result<SpotOrderStatus, DomainError>.Failure(
                    DomainError.Validation($"ID de orden Binance inválido: '{exchangeOrderId}'"));

            var response = await _retryPipeline.ExecuteAsync(async ct =>
            {
                var callResult = await _restClient.SpotApi.Trading.GetOrderAsync(
                    symbol: symbol,
                    orderId: binanceOrderId,
                    ct: ct);

                if (!callResult.Success)
                    throw new InvalidOperationException(
                        callResult.Error?.Message ?? "Error al consultar estado de orden.");

                return callResult;
            }, cancellationToken);

            if (!response.Success)
                return Result<SpotOrderStatus, DomainError>.Failure(
                    DomainError.ExternalService(response.Error?.Message ?? "Error al consultar orden."));

            var data = response.Data;

            return Result<SpotOrderStatus, DomainError>.Success(new SpotOrderStatus(
                ExchangeOrderId: data.Id.ToString(),
                ExecutedQuantity: data.QuantityFilled,
                ExecutedPrice: data.AverageFillPrice ?? data.Price,
                Status: data.Status.ToString(),
                IsCompletelyFilled: data.Status == BinanceEnums.OrderStatus.Filled,
                IsCancelled: data.Status is BinanceEnums.OrderStatus.Canceled
                                         or BinanceEnums.OrderStatus.Rejected
                                         or BinanceEnums.OrderStatus.Expired));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error al consultar estado de orden {ExchangeOrderId}", exchangeOrderId);
            return Result<SpotOrderStatus, DomainError>.Failure(
                DomainError.ExternalService(ex.Message));
        }
    }

    public async Task<Result<bool, DomainError>> CancelOrderAsync(
        string symbol,
        string exchangeOrderId,
        CancellationToken cancellationToken = default)
    {
        try
        {
            if (!long.TryParse(exchangeOrderId, out var binanceOrderId))
                return Result<bool, DomainError>.Failure(
                    DomainError.Validation($"ID de orden Binance inválido: '{exchangeOrderId}'"));

            var response = await _retryPipeline.ExecuteAsync(async ct =>
            {
                var callResult = await _restClient.SpotApi.Trading.CancelOrderAsync(
                    symbol: symbol,
                    orderId: binanceOrderId,
                    ct: ct);

                if (!callResult.Success)
                    throw new InvalidOperationException(
                        callResult.Error?.Message ?? "Error al cancelar orden.");

                return callResult;
            }, cancellationToken);

            if (!response.Success)
                return Result<bool, DomainError>.Failure(
                    DomainError.ExternalService(response.Error?.Message ?? "Error al cancelar orden en Binance."));

            _logger.LogInformation(
                "Orden {ExchangeOrderId} cancelada en Binance para {Symbol}",
                exchangeOrderId, symbol);

            return Result<bool, DomainError>.Success(true);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error al cancelar orden {ExchangeOrderId}", exchangeOrderId);
            return Result<bool, DomainError>.Failure(
                DomainError.ExternalService(ex.Message));
        }
    }

    private static BinanceEnums.SpotOrderType MapOrderType(CoreOrderType type) => type switch
    {
        CoreOrderType.Market    => BinanceEnums.SpotOrderType.Market,
        CoreOrderType.Limit     => BinanceEnums.SpotOrderType.Limit,
        CoreOrderType.StopLimit => BinanceEnums.SpotOrderType.StopLossLimit,
        _                       => BinanceEnums.SpotOrderType.Market
    };
}

/// <summary>
/// Excepción para errores de Binance que no deben reintentarse
/// (filtros, saldo insuficiente, timestamp inválido).
/// </summary>
internal sealed class BinanceNonRetryableException(int errorCode, string message)
    : Exception(message)
{
    public int ErrorCode { get; } = errorCode;
}
