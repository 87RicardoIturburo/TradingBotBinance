using global::Binance.Net.Interfaces.Clients;
using global::Binance.Net.Objects.Models.Spot.Socket;
using CryptoExchange.Net.Objects.Sockets;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TradingBot.Core.Interfaces;
using TradingBot.Core.Interfaces.Repositories;
using TradingBot.Core.Interfaces.Services;
using TradingBot.Core.ValueObjects;
using BinanceEnums = global::Binance.Net.Enums;

namespace TradingBot.Infrastructure.Binance;

/// <summary>
/// Escucha el User Data Stream de Binance vía WebSocket.
/// Procesa <c>executionReport</c> para mantener el estado de órdenes sincronizado
/// y <c>outboundAccountPosition</c> para invalidar la caché de balance.
/// CryptoExchange.Net gestiona el listenKey y su renovación automáticamente.
/// </summary>
internal sealed class UserDataStreamService : IUserDataStreamService, IHostedService, IAsyncDisposable
{
    private static readonly TimeSpan ReconnectDelay = TimeSpan.FromSeconds(5);

    private readonly IBinanceSocketClient _socketClient;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IAccountService      _accountService;
    private readonly BinanceOptions       _options;
    private readonly ILogger<UserDataStreamService> _logger;

    private UpdateSubscription? _subscription;
    private CancellationTokenSource? _cts;
    private volatile bool _isConnected;

    public bool IsConnected => _isConnected;

    public UserDataStreamService(
        IBinanceSocketClient              socketClient,
        IServiceScopeFactory              scopeFactory,
        IAccountService                   accountService,
        IOptions<BinanceOptions>          options,
        ILogger<UserDataStreamService>    logger)
    {
        _socketClient   = socketClient;
        _scopeFactory   = scopeFactory;
        _accountService = accountService;
        _options        = options.Value;
        _logger         = logger;
    }

    // ── IHostedService ────────────────────────────────────────────────────

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (!_options.HasCredentials)
        {
            _logger.LogInformation(
                "UserDataStreamService omitido: no hay API keys configuradas (modo Paper Trading)");
            return Task.CompletedTask;
        }

        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        // Ejecutar la conexión en background para no bloquear el arranque del host
        _ = Task.Run(() => ConnectAsync(_cts.Token), cancellationToken);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_cts is not null)
            await _cts.CancelAsync();

        if (_subscription is not null)
        {
            await _socketClient.UnsubscribeAsync(_subscription);
            _subscription = null;
        }

        _isConnected = false;
        _logger.LogInformation("UserDataStreamService detenido");
    }

    // ── IUserDataStreamService ────────────────────────────────────────────

    Task IUserDataStreamService.StartAsync(CancellationToken cancellationToken)
        => StartAsync(cancellationToken);

    Task IUserDataStreamService.StopAsync(CancellationToken cancellationToken)
        => StopAsync(cancellationToken);

    // ── Conexión y reconexión ─────────────────────────────────────────────

    private async Task ConnectAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                // CryptoExchange.Net gestiona el listenKey y su renovación internamente
                var subResult = await _socketClient.SpotApi.Account.SubscribeToUserDataUpdatesAsync(
                    onOrderUpdateMessage:        OnOrderUpdate,
                    onOcoOrderUpdateMessage:     null,
                    onAccountPositionMessage:    OnAccountPosition,
                    onAccountBalanceUpdate:      null,
                    onUserDataStreamTerminated:  OnStreamTerminated,
                    onBalanceLockUpdate:         null,
                    ct: cancellationToken);

                if (!subResult.Success || subResult.Data is null)
                {
                    var errorMsg = subResult.Error?.Message ?? "Unknown error";

                    // Errores de autenticación son permanentes: no reintentar
                    if (subResult.Error?.Code is -2015 or -2014
                        || errorMsg.Contains("API-key", StringComparison.OrdinalIgnoreCase)
                        || errorMsg.Contains("Unauthorized", StringComparison.OrdinalIgnoreCase))
                    {
                        _logger.LogError(
                            "User Data Stream: credenciales inválidas ({Error}). " +
                            "Verifique BINANCE_API_KEY / BINANCE_API_SECRET y que sean del entorno correcto " +
                            "(Testnet={UseTestnet}, Demo={UseDemo}). El servicio se detendrá hasta que se reinicie con keys válidas.",
                            errorMsg, _options.UseTestnet, _options.UseDemo);
                        return;
                    }

                    _logger.LogWarning(
                        "No se pudo suscribir al User Data Stream: {Error}. Reintentando en {Delay}s",
                        errorMsg, ReconnectDelay.TotalSeconds);
                    await Task.Delay(ReconnectDelay, cancellationToken);
                    continue;
                }

                _subscription = subResult.Data;
                _subscription.ConnectionLost     += () => OnConnectionLost(cancellationToken);
                _subscription.ConnectionRestored += _ => OnConnectionRestored();

                _isConnected = true;
                _logger.LogInformation("User Data Stream conectado");
                return;
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error al conectar User Data Stream. Reintentando en {Delay}s",
                    ReconnectDelay.TotalSeconds);
                await Task.Delay(ReconnectDelay, cancellationToken);
            }
        }
    }

    private void OnConnectionLost(CancellationToken cancellationToken)
    {
        _isConnected = false;
        _logger.LogWarning("User Data Stream desconectado. CryptoExchange.Net reconectará automáticamente.");
    }

    private void OnConnectionRestored()
    {
        _isConnected = true;
        _logger.LogInformation("User Data Stream reconectado");
    }

    private void OnStreamTerminated(DataEvent<global::Binance.Net.Objects.Models.BinanceStreamEvent> data)
    {
        _isConnected = false;
        _logger.LogWarning("User Data Stream terminado por Binance. Reconectando...");
        if (_cts is not null && !_cts.IsCancellationRequested)
            _ = Task.Run(async () => await ConnectAsync(_cts.Token));
    }

    // ── Procesamiento de eventos ──────────────────────────────────────────

    private void OnOrderUpdate(DataEvent<BinanceStreamOrderUpdate> data)
    {
        var update = data.Data;
        _logger.LogDebug(
            "executionReport: BinanceId={BinanceId} Symbol={Symbol} Status={Status} FilledQty={FilledQty}",
            update.Id, update.Symbol, update.Status, update.QuantityFilled);

        _ = Task.Run(async () => await ProcessOrderUpdateAsync(update));
    }

    private async Task ProcessOrderUpdateAsync(BinanceStreamOrderUpdate update)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var orderRepo  = scope.ServiceProvider.GetRequiredService<IOrderRepository>();
            var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();

            var orders = await orderRepo.GetPendingSyncAsync();
            var order  = orders.FirstOrDefault(o =>
                o.BinanceOrderId == update.Id.ToString());

            if (order is null)
            {
                _logger.LogDebug("No se encontró orden local para BinanceId={BinanceId}", update.Id);
                return;
            }

            var tracked = await orderRepo.GetByIdAsync(order.Id);
            if (tracked is null) return;

            // Precio promedio = total ejecutado en quote / total ejecutado en base
            var avgPrice = update.QuantityFilled > 0
                ? update.QuoteQuantityFilled / update.QuantityFilled
                : update.LastPriceFilled;

            var filledQty     = Quantity.Create(update.QuantityFilled);
            var executedPrice = Price.Create(avgPrice);

            if (filledQty.IsFailure || executedPrice.IsFailure)
            {
                _logger.LogWarning("Datos inválidos en executionReport para BinanceId={Id}", update.Id);
                return;
            }

            switch (update.Status)
            {
                case BinanceEnums.OrderStatus.Filled:
                    tracked.Fill(filledQty.Value, executedPrice.Value);
                    break;

                case BinanceEnums.OrderStatus.PartiallyFilled:
                    tracked.PartialFill(filledQty.Value, executedPrice.Value);
                    break;

                case BinanceEnums.OrderStatus.Canceled:
                case BinanceEnums.OrderStatus.Expired:
                case BinanceEnums.OrderStatus.Rejected:
                    tracked.Cancel($"Estado Binance: {update.Status}");
                    break;
            }

            await orderRepo.UpdateAsync(tracked);
            await unitOfWork.SaveChangesAsync();

            _logger.LogInformation(
                "Orden {OrderId} actualizada vía User Data Stream → {Status}",
                tracked.Id, tracked.Status);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error procesando executionReport para BinanceId={BinanceId}", update.Id);
        }
    }

    private void OnAccountPosition(DataEvent<BinanceStreamPositionsUpdate> data)
    {
        _logger.LogDebug("outboundAccountPosition recibido — invalidando caché de balance");
        _ = Task.Run(async () =>
        {
            try
            {
                await _accountService.InvalidateCacheAsync();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error invalidando caché de balance");
            }
        });
    }

    public async ValueTask DisposeAsync()
    {
        if (_cts is not null)
        {
            await _cts.CancelAsync();
            _cts.Dispose();
        }
    }
}
