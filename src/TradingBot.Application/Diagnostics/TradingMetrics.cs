using System.Diagnostics.Metrics;

namespace TradingBot.Application.Diagnostics;

/// <summary>
/// Métricas de trading instrumentadas con <see cref="System.Diagnostics.Metrics"/>.
/// Compatible con OpenTelemetry, Prometheus y cualquier exportador de métricas .NET.
/// Mantiene contadores internos legibles para el dashboard en tiempo real vía SignalR.
/// </summary>
public sealed class TradingMetrics
{
    public const string MeterName = "TradingBot";

    private readonly Counter<long> _ticksProcessed;
    private readonly Counter<long> _signalsGenerated;
    private readonly Counter<long> _ordersPlaced;
    private readonly Counter<long> _ordersFailed;
    private readonly Histogram<double> _tickToOrderLatency;
    private readonly Counter<long> _ticksDropped;
    private readonly ObservableGauge<double> _dailyPnL;

    private double _currentDailyPnL;

    // ── Contadores internos legibles para snapshot del dashboard ──────────
    private long _totalTicksProcessed;
    private long _totalSignalsGenerated;
    private long _totalOrdersPlaced;
    private long _totalOrdersFailed;
    private long _totalTicksDropped;
    private long _totalOrdersPaper;
    private long _totalOrdersLive;
    private double _lastLatencyMs;
    private double _sumLatencyMs;
    private long _latencyCount;

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

        _ticksDropped = meter.CreateCounter<long>(
            "trading.ticks_dropped",
            unit: "{tick}",
            description: "Número de ticks descartados por canal lleno (DropOldest)");

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
    {
        _ticksProcessed.Add(1, new KeyValuePair<string, object?>("symbol", symbol));
        Interlocked.Increment(ref _totalTicksProcessed);
    }

    /// <summary>Registra una señal generada por una estrategia.</summary>
    public void RecordSignalGenerated(string strategyName, string symbol, string direction)
    {
        _signalsGenerated.Add(1,
            new KeyValuePair<string, object?>("strategy", strategyName),
            new KeyValuePair<string, object?>("symbol", symbol),
            new KeyValuePair<string, object?>("direction", direction));
        Interlocked.Increment(ref _totalSignalsGenerated);
    }

    /// <summary>Registra una orden colocada exitosamente.</summary>
    public void RecordOrderPlaced(string symbol, string side, string type, bool isPaper)
    {
        _ordersPlaced.Add(1,
            new KeyValuePair<string, object?>("symbol", symbol),
            new KeyValuePair<string, object?>("side", side),
            new KeyValuePair<string, object?>("type", type),
            new KeyValuePair<string, object?>("paper", isPaper));
        Interlocked.Increment(ref _totalOrdersPlaced);
        if (isPaper)
            Interlocked.Increment(ref _totalOrdersPaper);
        else
            Interlocked.Increment(ref _totalOrdersLive);
    }

    /// <summary>Registra una orden rechazada.</summary>
    public void RecordOrderFailed(string symbol, string reason)
    {
        _ordersFailed.Add(1,
            new KeyValuePair<string, object?>("symbol", symbol),
            new KeyValuePair<string, object?>("reason", reason));
        Interlocked.Increment(ref _totalOrdersFailed);
    }

    /// <summary>Registra la latencia tick→orden en milisegundos.</summary>
    public void RecordTickToOrderLatency(double milliseconds, string symbol)
    {
        _tickToOrderLatency.Record(milliseconds,
            new KeyValuePair<string, object?>("symbol", symbol));
        Volatile.Write(ref _lastLatencyMs, milliseconds);
        Interlocked.Increment(ref _latencyCount);
        // Aproximación acumulativa (no necesita ser atómica; solo para promedios del dashboard)
        _sumLatencyMs += milliseconds;
    }

    /// <summary>Registra un tick descartado porque el canal bounded estaba lleno.</summary>
    public void RecordTickDropped(string symbol, string streamType)
    {
        _ticksDropped.Add(1,
            new KeyValuePair<string, object?>("symbol", symbol),
            new KeyValuePair<string, object?>("stream", streamType));
        Interlocked.Increment(ref _totalTicksDropped);
    }

    /// <summary>Actualiza el P&amp;L diario acumulado.</summary>
    public void UpdateDailyPnL(double pnlUsdt)
        => _currentDailyPnL = pnlUsdt;

    /// <summary>Captura un snapshot inmutable de las métricas actuales para el dashboard.</summary>
    public MetricsSnapshot GetSnapshot() => new(
        TotalTicksProcessed: Interlocked.Read(ref _totalTicksProcessed),
        TotalSignalsGenerated: Interlocked.Read(ref _totalSignalsGenerated),
        TotalOrdersPlaced: Interlocked.Read(ref _totalOrdersPlaced),
        TotalOrdersFailed: Interlocked.Read(ref _totalOrdersFailed),
        TotalTicksDropped: Interlocked.Read(ref _totalTicksDropped),
        TotalOrdersPaper: Interlocked.Read(ref _totalOrdersPaper),
        TotalOrdersLive: Interlocked.Read(ref _totalOrdersLive),
        LastLatencyMs: Volatile.Read(ref _lastLatencyMs),
        AverageLatencyMs: _latencyCount > 0 ? _sumLatencyMs / _latencyCount : 0,
        DailyPnLUsdt: _currentDailyPnL,
        Timestamp: DateTimeOffset.UtcNow);
}

/// <summary>Snapshot inmutable de las métricas de trading para el dashboard.</summary>
public sealed record MetricsSnapshot(
    long           TotalTicksProcessed,
    long           TotalSignalsGenerated,
    long           TotalOrdersPlaced,
    long           TotalOrdersFailed,
    long           TotalTicksDropped,
    long           TotalOrdersPaper,
    long           TotalOrdersLive,
    double         LastLatencyMs,
    double         AverageLatencyMs,
    double         DailyPnLUsdt,
    DateTimeOffset Timestamp);
