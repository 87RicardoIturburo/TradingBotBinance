using System.Diagnostics.Metrics;

namespace TradingBot.Application.Diagnostics;

/// <summary>
/// Métricas de trading instrumentadas con <see cref="System.Diagnostics.Metrics"/>.
/// Compatible con OpenTelemetry, Prometheus y cualquier exportador de métricas .NET.
/// </summary>
public sealed class TradingMetrics
{
    public const string MeterName = "TradingBot";

    private readonly Counter<long> _ticksProcessed;
    private readonly Counter<long> _signalsGenerated;
    private readonly Counter<long> _ordersPlaced;
    private readonly Counter<long> _ordersFailed;
    private readonly Histogram<double> _tickToOrderLatency;
    private readonly ObservableGauge<double> _dailyPnL;

    private double _currentDailyPnL;

    public TradingMetrics(IMeterFactory meterFactory)
    {
        var meter = meterFactory.Create(MeterName);

        _ticksProcessed = meter.CreateCounter<long>(
            "trading.ticks_processed",
            unit: "{tick}",
            description: "Número de ticks de mercado procesados");

        _signalsGenerated = meter.CreateCounter<long>(
            "trading.signals_generated",
            unit: "{signal}",
            description: "Número de señales de trading generadas");

        _ordersPlaced = meter.CreateCounter<long>(
            "trading.orders_placed",
            unit: "{order}",
            description: "Número de órdenes colocadas exitosamente");

        _ordersFailed = meter.CreateCounter<long>(
            "trading.orders_failed",
            unit: "{order}",
            description: "Número de órdenes rechazadas por riesgo, filtros o exchange");

        _tickToOrderLatency = meter.CreateHistogram<double>(
            "trading.tick_to_order_latency",
            unit: "ms",
            description: "Latencia desde recepción del tick hasta colocación de la orden");

        _dailyPnL = meter.CreateObservableGauge(
            "trading.pnl_daily",
            observeValue: () => _currentDailyPnL,
            unit: "USDT",
            description: "P&L diario realizado acumulado");
    }

    /// <summary>Registra un tick procesado para un símbolo.</summary>
    public void RecordTickProcessed(string symbol)
        => _ticksProcessed.Add(1, new KeyValuePair<string, object?>("symbol", symbol));

    /// <summary>Registra una señal generada por una estrategia.</summary>
    public void RecordSignalGenerated(string strategyName, string symbol, string direction)
        => _signalsGenerated.Add(1,
            new KeyValuePair<string, object?>("strategy", strategyName),
            new KeyValuePair<string, object?>("symbol", symbol),
            new KeyValuePair<string, object?>("direction", direction));

    /// <summary>Registra una orden colocada exitosamente.</summary>
    public void RecordOrderPlaced(string symbol, string side, string type, bool isPaper)
        => _ordersPlaced.Add(1,
            new KeyValuePair<string, object?>("symbol", symbol),
            new KeyValuePair<string, object?>("side", side),
            new KeyValuePair<string, object?>("type", type),
            new KeyValuePair<string, object?>("paper", isPaper));

    /// <summary>Registra una orden rechazada.</summary>
    public void RecordOrderFailed(string symbol, string reason)
        => _ordersFailed.Add(1,
            new KeyValuePair<string, object?>("symbol", symbol),
            new KeyValuePair<string, object?>("reason", reason));

    /// <summary>Registra la latencia tick→orden en milisegundos.</summary>
    public void RecordTickToOrderLatency(double milliseconds, string symbol)
        => _tickToOrderLatency.Record(milliseconds,
            new KeyValuePair<string, object?>("symbol", symbol));

    /// <summary>Actualiza el P&amp;L diario acumulado.</summary>
    public void UpdateDailyPnL(double pnlUsdt)
        => _currentDailyPnL = pnlUsdt;
}
