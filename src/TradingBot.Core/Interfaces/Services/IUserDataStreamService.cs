namespace TradingBot.Core.Interfaces.Services;

/// <summary>
/// Gestiona el WebSocket de User Data Stream de Binance.
/// Recibe en tiempo real:
/// - <c>executionReport</c>  → actualiza estado de órdenes en BD
/// - <c>outboundAccountPosition</c> → invalida caché de balance
/// El listenKey se renueva automáticamente cada 30 minutos.
/// </summary>
public interface IUserDataStreamService
{
    /// <summary>Indica si el stream está conectado y procesando eventos.</summary>
    bool IsConnected { get; }

    /// <summary>Inicia la conexión al User Data Stream y comienza a procesar eventos.</summary>
    Task StartAsync(CancellationToken cancellationToken = default);

    /// <summary>Detiene la conexión de forma controlada.</summary>
    Task StopAsync(CancellationToken cancellationToken = default);
}
