using TradingBot.Core.Events;

namespace TradingBot.Core.Interfaces.Services;

/// <summary>
/// Notificador de eventos en tiempo real hacia clientes conectados (e.g. SignalR).
/// La implementación vive en la capa API; la capa Application solo consume la interfaz.
/// </summary>
public interface ITradingNotifier
{
    /// <summary>Envía un tick de mercado a todos los clientes.</summary>
    Task NotifyMarketTickAsync(MarketTickReceivedEvent tick, CancellationToken cancellationToken = default);

    /// <summary>Notifica que se ejecutó una orden.</summary>
    Task NotifyOrderExecutedAsync(OrderPlacedEvent order, CancellationToken cancellationToken = default);

    /// <summary>Notifica que se generó una señal de trading.</summary>
    Task NotifySignalGeneratedAsync(SignalGeneratedEvent signal, CancellationToken cancellationToken = default);

    /// <summary>Envía una alerta de sistema o riesgo.</summary>
    Task NotifyAlertAsync(string message, CancellationToken cancellationToken = default);

    /// <summary>Notifica que una estrategia fue actualizada (hot-reload).</summary>
    Task NotifyStrategyUpdatedAsync(StrategyUpdatedEvent update, CancellationToken cancellationToken = default);

    /// <summary>Envía resultados actualizados del Market Scanner.</summary>
    Task NotifyScannerUpdateAsync(IReadOnlyList<SymbolScore> scores, CancellationToken cancellationToken = default);

    /// <summary>Envía snapshot del pool dinámico de símbolos (AutoPilot v2).</summary>
    Task NotifySymbolPoolUpdateAsync(SymbolPoolSnapshot snapshot, CancellationToken cancellationToken = default);
}
