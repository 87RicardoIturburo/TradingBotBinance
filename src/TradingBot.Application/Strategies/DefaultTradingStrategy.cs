using Microsoft.Extensions.Logging;
using TradingBot.Application.Strategies.Indicators;
using TradingBot.Core.Common;
using TradingBot.Core.Entities;
using TradingBot.Core.Enums;
using TradingBot.Core.Events;
using TradingBot.Core.Interfaces.Trading;
using TradingBot.Core.ValueObjects;

namespace TradingBot.Application.Strategies;

/// <summary>
/// Implementación por defecto de <see cref="ITradingStrategy"/>.
/// Alimenta indicadores con cada tick y genera señales cuando se cruzan umbrales.
/// </summary>
internal sealed class DefaultTradingStrategy : ITradingStrategy
{
    private readonly ILogger<DefaultTradingStrategy> _logger;
    private readonly Dictionary<IndicatorType, ITechnicalIndicator> _indicators = [];

    private TradingStrategy? _config;
    private decimal? _previousRsi;
    private decimal? _previousMacdHistogram;
    private DateTimeOffset _lastSignalAt;

    /// <summary>Tiempo mínimo entre señales consecutivas. Evita ráfagas de órdenes.</summary>
    internal static readonly TimeSpan SignalCooldown = TimeSpan.FromMinutes(1);

    public Guid        StrategyId    { get; private set; }
    public Symbol      Symbol        { get; private set; } = null!;
    public TradingMode Mode          { get; private set; }
    public bool        IsInitialized { get; private set; }

    public DefaultTradingStrategy(ILogger<DefaultTradingStrategy> logger)
    {
        _logger = logger;
    }

    public Task InitializeAsync(TradingStrategy config, CancellationToken cancellationToken = default)
    {
        _config    = config;
        StrategyId = config.Id;
        Symbol     = config.Symbol;
        Mode       = config.Mode;

        RebuildIndicators(config);
        IsInitialized = true;

        _logger.LogInformation(
            "Estrategia '{Name}' ({Id}) inicializada con {Count} indicadores",
            config.Name, config.Id, _indicators.Count);

        return Task.CompletedTask;
    }

    public Task<Result<SignalGeneratedEvent?, DomainError>> ProcessTickAsync(
        MarketTickReceivedEvent tick,
        CancellationToken cancellationToken = default)
    {
        if (!IsInitialized || _config is null)
            return Task.FromResult(
                Result<SignalGeneratedEvent?, DomainError>.Failure(
                    DomainError.InvalidOperation("La estrategia no está inicializada.")));

        var price = tick.LastPrice.Value;

        foreach (var indicator in _indicators.Values)
            indicator.Update(price);

        var signal = EvaluateSignal(tick);

        return Task.FromResult(
            Result<SignalGeneratedEvent?, DomainError>.Success(signal));
    }

    public Task ReloadConfigAsync(TradingStrategy config, CancellationToken cancellationToken = default)
    {
        _config = config;
        Mode    = config.Mode;
        RebuildIndicators(config);

        _logger.LogInformation(
            "Hot-reload completado para estrategia '{Name}' ({Id})", config.Name, config.Id);

        return Task.CompletedTask;
    }

    public void Reset()
    {
        foreach (var indicator in _indicators.Values)
            indicator.Reset();

        _previousRsi           = null;
        _previousMacdHistogram = null;
        _lastSignalAt          = default;
        IsInitialized          = false;
    }

    private void RebuildIndicators(TradingStrategy config)
    {
        _indicators.Clear();
        _previousRsi           = null;
        _previousMacdHistogram = null;

        foreach (var indicatorConfig in config.Indicators)
        {
            try
            {
                var indicator = IndicatorFactory.Create(indicatorConfig);
                _indicators[indicatorConfig.Type] = indicator;
            }
            catch (NotSupportedException ex)
            {
                _logger.LogWarning(ex, "Indicador no soportado: {Type}", indicatorConfig.Type);
            }
        }
    }

    private SignalGeneratedEvent? EvaluateSignal(MarketTickReceivedEvent tick)
    {
        // RSI es el generador primario de señales — sin él no hay decisión
        if (!_indicators.TryGetValue(IndicatorType.RSI, out var rsiIndicator) || !rsiIndicator.IsReady)
            return null;

        var rsi    = rsiIndicator.Calculate()!.Value;
        var config = _config!.Indicators.FirstOrDefault(i => i.Type == IndicatorType.RSI);
        if (config is null) return null;

        var oversold   = config.GetParameter("oversold", 30);
        var overbought = config.GetParameter("overbought", 70);
        var price      = tick.LastPrice.Value;

        // Determinar señal candidata del RSI (cruce de umbral, no repetir)
        OrderSide? candidateSide = null;

        if (rsi <= oversold && (_previousRsi is null || _previousRsi > oversold))
            candidateSide = OrderSide.Buy;
        else if (rsi >= overbought && (_previousRsi is null || _previousRsi < overbought))
            candidateSide = OrderSide.Sell;

        _previousRsi = rsi;

        if (candidateSide is null)
            return null;

        // Cooldown: evitar ráfagas de señales en períodos volátiles
        if (_lastSignalAt != default && tick.Timestamp - _lastSignalAt < SignalCooldown)
            return null;

        // Confirmación multi-indicador: cada indicador presente emite un voto
        var (confirms, total) = CountConfirmations(candidateSide.Value, price);

        // Si no hay confirmadores adicionales, RSI decide solo.
        // Si hay confirmadores, se requiere mayoría simple.
        if (total > 0 && confirms * 2 < total)
        {
            _logger.LogDebug(
                "Señal RSI {Side} descartada: confirmación {Confirms}/{Total}",
                candidateSide, confirms, total);
            return null;
        }

        _lastSignalAt = tick.Timestamp;

        return new SignalGeneratedEvent(
            StrategyId, Symbol, candidateSide.Value, tick.LastPrice,
            BuildSnapshot(candidateSide.Value, confirms, total));
    }

    /// <summary>
    /// Cuenta cuántos indicadores adicionales confirman la señal candidata.
    /// Devuelve (confirmaciones, total_votantes). Indicadores no listos se excluyen.
    /// </summary>
    internal (int Confirms, int Total) CountConfirmations(OrderSide side, decimal price)
    {
        var confirms = 0;
        var total    = 0;

        // MACD: histograma positivo = momentum alcista
        if (_indicators.TryGetValue(IndicatorType.MACD, out var macdRaw)
            && macdRaw is MacdIndicator macd && macd.IsReady)
        {
            total++;
            var histogram = macd.Histogram ?? 0m;

            if (side == OrderSide.Buy && histogram > 0m) confirms++;
            else if (side == OrderSide.Sell && histogram < 0m) confirms++;

            _previousMacdHistogram = histogram;
        }

        // Bollinger Bands: precio ≤ Lower = sobreventa, ≥ Upper = sobrecompra
        if (_indicators.TryGetValue(IndicatorType.BollingerBands, out var bbRaw)
            && bbRaw is BollingerBandsIndicator bb && bb.IsReady)
        {
            total++;
            if (side == OrderSide.Buy && price <= bb.LowerBand!.Value) confirms++;
            else if (side == OrderSide.Sell && price >= bb.UpperBand!.Value) confirms++;
        }

        // EMA: precio > EMA = tendencia alcista
        if (_indicators.TryGetValue(IndicatorType.EMA, out var ema) && ema.IsReady)
        {
            total++;
            var emaValue = ema.Calculate()!.Value;
            if (side == OrderSide.Buy && price > emaValue) confirms++;
            else if (side == OrderSide.Sell && price < emaValue) confirms++;
        }

        // SMA: precio > SMA = tendencia alcista
        if (_indicators.TryGetValue(IndicatorType.SMA, out var sma) && sma.IsReady)
        {
            total++;
            var smaValue = sma.Calculate()!.Value;
            if (side == OrderSide.Buy && price > smaValue) confirms++;
            else if (side == OrderSide.Sell && price < smaValue) confirms++;
        }

        // Linear Regression: slope > 0 = tendencia alcista con R² > 0.5
        if (_indicators.TryGetValue(IndicatorType.LinearRegression, out var lrRaw)
            && lrRaw is LinearRegressionIndicator lr && lr.IsReady)
        {
            total++;
            var slope    = lr.Slope ?? 0m;
            var rSquared = lr.RSquared ?? 0m;

            if (rSquared > 0.5m)
            {
                if (side == OrderSide.Buy && slope > 0m) confirms++;
                else if (side == OrderSide.Sell && slope < 0m) confirms++;
            }
            // R² bajo = tendencia débil, no confirma ni rechaza
        }

        // Fibonacci: precio cerca de un nivel de soporte/resistencia
        if (_indicators.TryGetValue(IndicatorType.Fibonacci, out var fibRaw)
            && fibRaw is FibonacciIndicator fib && fib.IsReady)
        {
            total++;
            var nearLevel = fib.GetNearestLevel(price);

            if (nearLevel is not null)
            {
                // Cerca de un nivel Fibonacci = zona de reversión potencial
                if (side == OrderSide.Buy && nearLevel >= 0.618m) confirms++;   // Retroceso profundo = soporte fuerte
                else if (side == OrderSide.Sell && nearLevel <= 0.382m) confirms++; // Retroceso leve = resistencia
            }
        }

        return (confirms, total);
    }

    private string BuildSnapshot(OrderSide? side = null, int confirms = 0, int total = 0)
    {
        var parts = _indicators
            .Where(kv => kv.Value.IsReady)
            .Select(kv => $"{kv.Value.Name}={kv.Value.Calculate():F4}");

        var snapshot = string.Join(" | ", parts);

        if (side is not null && total > 0)
            snapshot += $" | Confirm={confirms}/{total}";

        return snapshot;
    }
}
