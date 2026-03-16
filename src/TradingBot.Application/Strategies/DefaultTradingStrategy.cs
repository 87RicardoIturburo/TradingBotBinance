using System.Globalization;
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
    private int _previousEmaRelation; // -1 = price below EMA, 0 = unknown, 1 = price above EMA
    private DateTimeOffset _lastSignalAt;
    private MarketRegimeResult? _lastRegime;

    /// <summary>Tiempo mínimo entre señales consecutivas. Evita ráfagas de órdenes.</summary>
    internal static readonly TimeSpan SignalCooldown = TimeSpan.FromMinutes(1);

    public Guid        StrategyId    { get; private set; }
    public Symbol      Symbol        { get; private set; } = null!;
    public TradingMode Mode          { get; private set; }
    public bool        IsInitialized { get; private set; }
    public MarketRegime CurrentRegime => _lastRegime?.Regime ?? MarketRegime.Unknown;
    public decimal?    CurrentAtrValue =>
        _indicators.TryGetValue(IndicatorType.ATR, out var atrInd) && atrInd.IsReady
            ? atrInd.Calculate() : null;

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
        _previousEmaRelation   = 0;
        _lastSignalAt          = default;
        _lastRegime            = null;
        IsInitialized          = false;
    }

    public void WarmUpPrice(decimal price)
    {
        foreach (var indicator in _indicators.Values)
            indicator.Update(price);
    }

    public string GetCurrentSnapshot() => BuildSnapshot();

    /// <summary>
    /// Establece el estado previo de los indicadores principales para evitar
    /// señales falsas en el primer tick tras el warm-up.
    /// Debe llamarse UNA vez después de completar el warm-up y ANTES de procesar ticks.
    /// </summary>
    internal void SyncPreviousIndicatorState()
    {
        if (_indicators.TryGetValue(IndicatorType.RSI, out var rsi) && rsi.IsReady)
            _previousRsi = rsi.Calculate();

        if (_indicators.TryGetValue(IndicatorType.MACD, out var macdRaw)
            && macdRaw is MacdIndicator macd && macd.IsReady)
            _previousMacdHistogram = macd.Histogram;
    }

    private void RebuildIndicators(TradingStrategy config)
    {
        _indicators.Clear();
        _previousRsi           = null;
        _previousMacdHistogram = null;
        _previousEmaRelation   = 0;
        _lastRegime            = null;

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
        var price = tick.LastPrice.Value;

        // Detectar régimen de mercado
        var adx = _indicators.TryGetValue(IndicatorType.ADX, out var adxRaw) ? adxRaw as AdxIndicator : null;
        var bb = _indicators.TryGetValue(IndicatorType.BollingerBands, out var bbRaw) ? bbRaw as BollingerBandsIndicator : null;
        var atr = _indicators.TryGetValue(IndicatorType.ATR, out var atrRaw) ? atrRaw as AtrIndicator : null;
        _lastRegime = MarketRegimeDetector.Detect(adx, bb, atr, price);

        // Alta volatilidad → no generar señales (protección)
        if (_lastRegime.Regime == MarketRegime.HighVolatility)
        {
            UpdatePreviousIndicatorState();
            _logger.LogDebug("Señal suprimida: régimen HighVolatility (ADX={Adx}, BW={BW})",
                _lastRegime.AdxValue?.ToString("F1"), _lastRegime.BandWidth?.ToString("F4"));
            return null;
        }

        // Determinar señal candidata — el primer generador disponible decide, los demás confirman
        var (candidateSide, signalSource) = DetermineSignalCandidate(price);

        if (candidateSide is null)
        {
            UpdatePreviousIndicatorState();
            return null;
        }

        // Trending → solo operar en la dirección de la tendencia (ADX + DI)
        if (_lastRegime.Regime == MarketRegime.Trending && adx is { IsReady: true })
        {
            if (candidateSide == OrderSide.Buy && adx.IsBearish)
            {
                _logger.LogDebug(
                    "Señal Buy descartada: tendencia bajista (+DI={PlusDi:F1}, -DI={MinusDi:F1})",
                    adx.PlusDi, adx.MinusDi);
                UpdatePreviousIndicatorState();
                return null;
            }
            if (candidateSide == OrderSide.Sell && adx.IsBullish)
            {
                _logger.LogDebug(
                    "Señal Sell descartada: tendencia alcista (+DI={PlusDi:F1}, -DI={MinusDi:F1})",
                    adx.PlusDi, adx.MinusDi);
                UpdatePreviousIndicatorState();
                return null;
            }
        }

        // Cooldown: evitar ráfagas de señales en períodos volátiles
        if (_lastSignalAt != default && tick.Timestamp - _lastSignalAt < SignalCooldown)
            return null;

        // Confirmación multi-indicador: indicadores que NO son el generador emiten un voto
        var (confirms, total) = CountConfirmations(candidateSide.Value, price, signalSource);

        // Si no hay confirmadores adicionales, el generador decide solo.
        // Si hay confirmadores, se requiere mayoría simple.
        if (total > 0 && confirms * 2 < total)
        {
            _logger.LogDebug(
                "Señal {Source} {Side} descartada: confirmación {Confirms}/{Total}",
                signalSource, candidateSide, confirms, total);
            return null;
        }

        _lastSignalAt = tick.Timestamp;

        // Obtener ATR actual para position sizing dinámico
        var currentAtr = _indicators.TryGetValue(IndicatorType.ATR, out var atrInd)
                         && atrInd is AtrIndicator atrCalc && atrCalc.IsReady
            ? atrCalc.Value
            : null;

        return new SignalGeneratedEvent(
            StrategyId, Symbol, candidateSide.Value, tick.LastPrice,
            BuildSnapshot(candidateSide.Value, confirms, total),
            currentAtr);
    }

    /// <summary>
    /// Determina la señal candidata usando el primer generador primario disponible.
    /// Prioridad: RSI → MACD → Bollinger → EMA → SMA.
    /// Devuelve la dirección candidata y el tipo de indicador que la generó.
    /// </summary>
    private (OrderSide? Side, IndicatorType Source) DetermineSignalCandidate(decimal price)
    {
        // 1. RSI: cruce de oversold/overbought
        if (_indicators.TryGetValue(IndicatorType.RSI, out var rsiIndicator) && rsiIndicator.IsReady)
        {
            var rsi = rsiIndicator.Calculate()!.Value;
            var config = _config!.Indicators.FirstOrDefault(i => i.Type == IndicatorType.RSI);
            if (config is not null)
            {
                var oversold   = config.GetParameter("oversold", 30);
                var overbought = config.GetParameter("overbought", 70);

                OrderSide? rsiSide = null;
                if (rsi <= oversold && (_previousRsi is null || _previousRsi > oversold))
                    rsiSide = OrderSide.Buy;
                else if (rsi >= overbought && (_previousRsi is null || _previousRsi < overbought))
                    rsiSide = OrderSide.Sell;

                _previousRsi = rsi;

                if (rsiSide is not null)
                    return (rsiSide, IndicatorType.RSI);
            }
            else
            {
                _previousRsi = rsiIndicator.Calculate();
            }
        }

        // 2. MACD: cruce de histograma (cambio de signo)
        if (_indicators.TryGetValue(IndicatorType.MACD, out var macdRaw)
            && macdRaw is MacdIndicator macd && macd.IsReady)
        {
            var histogram = macd.Histogram ?? 0m;
            OrderSide? macdSide = null;

            if (_previousMacdHistogram is not null)
            {
                // Histograma cruza de negativo a positivo → momentum alcista → Buy
                if (histogram > 0m && _previousMacdHistogram.Value <= 0m)
                    macdSide = OrderSide.Buy;
                // Histograma cruza de positivo a negativo → momentum bajista → Sell
                else if (histogram < 0m && _previousMacdHistogram.Value >= 0m)
                    macdSide = OrderSide.Sell;
            }

            _previousMacdHistogram = histogram;

            if (macdSide is not null)
                return (macdSide, IndicatorType.MACD);
        }

        // 3. Bollinger Bands: precio toca banda inferior/superior
        if (_indicators.TryGetValue(IndicatorType.BollingerBands, out var bbIndicator)
            && bbIndicator is BollingerBandsIndicator bbGen && bbGen.IsReady)
        {
            if (price <= bbGen.LowerBand!.Value)
                return (OrderSide.Buy, IndicatorType.BollingerBands);
            if (price >= bbGen.UpperBand!.Value)
                return (OrderSide.Sell, IndicatorType.BollingerBands);
        }

        // 4. EMA: precio cruza la EMA (requiere estado previo implícito via dirección)
        if (_indicators.TryGetValue(IndicatorType.EMA, out var emaIndicator) && emaIndicator.IsReady)
        {
            var emaValue = emaIndicator.Calculate()!.Value;
            if (price > emaValue && _previousEmaRelation == -1)
            {
                _previousEmaRelation = 1;
                return (OrderSide.Buy, IndicatorType.EMA);
            }
            if (price < emaValue && _previousEmaRelation == 1)
            {
                _previousEmaRelation = -1;
                return (OrderSide.Sell, IndicatorType.EMA);
            }
            _previousEmaRelation = price >= emaValue ? 1 : -1;
        }

        // Sin RSI ni ningún generador disponible → no hay señal
        return (null, default);
    }

    /// <summary>Actualiza el estado previo de indicadores cuando no se genera señal.</summary>
    private void UpdatePreviousIndicatorState()
    {
        if (_indicators.TryGetValue(IndicatorType.RSI, out var rsi) && rsi.IsReady)
            _previousRsi = rsi.Calculate();

        if (_indicators.TryGetValue(IndicatorType.MACD, out var macdRaw)
            && macdRaw is MacdIndicator macd && macd.IsReady)
            _previousMacdHistogram = macd.Histogram;

        if (_indicators.TryGetValue(IndicatorType.EMA, out var ema) && ema.IsReady)
            _previousEmaRelation = ema.Calculate()!.Value <= 0 ? 0 : (_previousEmaRelation == 0 ? 0 : _previousEmaRelation);
    }

    /// <summary>
    /// Cuenta cuántos indicadores adicionales confirman la señal candidata.
    /// Devuelve (confirmaciones, total_votantes). El indicador que generó la señal se excluye.
    /// Indicadores no listos se excluyen.
    /// </summary>
    internal (int Confirms, int Total) CountConfirmations(OrderSide side, decimal price, IndicatorType signalSource = default)
    {
        var confirms = 0;
        var total    = 0;

        // MACD: histograma positivo = momentum alcista
        if (signalSource != IndicatorType.MACD
            && _indicators.TryGetValue(IndicatorType.MACD, out var macdRaw)
            && macdRaw is MacdIndicator macd && macd.IsReady)
        {
            total++;
            var histogram = macd.Histogram ?? 0m;

            if (side == OrderSide.Buy && histogram > 0m) confirms++;
            else if (side == OrderSide.Sell && histogram < 0m) confirms++;
        }

        // Bollinger Bands: precio ≤ Lower = sobreventa, ≥ Upper = sobrecompra
        if (signalSource != IndicatorType.BollingerBands
            && _indicators.TryGetValue(IndicatorType.BollingerBands, out var bbRaw2)
            && bbRaw2 is BollingerBandsIndicator bb2 && bb2.IsReady)
        {
            total++;
            if (side == OrderSide.Buy && price <= bb2.LowerBand!.Value) confirms++;
            else if (side == OrderSide.Sell && price >= bb2.UpperBand!.Value) confirms++;
        }

        // EMA: precio > EMA = tendencia alcista
        if (signalSource != IndicatorType.EMA
            && _indicators.TryGetValue(IndicatorType.EMA, out var ema) && ema.IsReady)
        {
            total++;
            var emaValue = ema.Calculate()!.Value;
            if (side == OrderSide.Buy && price > emaValue) confirms++;
            else if (side == OrderSide.Sell && price < emaValue) confirms++;
        }

        // SMA: precio > SMA = tendencia alcista
        if (signalSource != IndicatorType.SMA
            && _indicators.TryGetValue(IndicatorType.SMA, out var sma) && sma.IsReady)
        {
            total++;
            var smaValue = sma.Calculate()!.Value;
            if (side == OrderSide.Buy && price > smaValue) confirms++;
            else if (side == OrderSide.Sell && price < smaValue) confirms++;
        }

        // RSI como confirmador (cuando otro indicador generó la señal)
        if (signalSource != IndicatorType.RSI
            && _indicators.TryGetValue(IndicatorType.RSI, out var rsiConf) && rsiConf.IsReady)
        {
            total++;
            var rsiVal = rsiConf.Calculate()!.Value;
            var rsiConfig = _config!.Indicators.FirstOrDefault(i => i.Type == IndicatorType.RSI);
            var os = rsiConfig?.GetParameter("oversold", 30) ?? 30;
            var ob = rsiConfig?.GetParameter("overbought", 70) ?? 70;
            if (side == OrderSide.Buy && rsiVal <= os + 10) confirms++;      // RSI en zona baja
            else if (side == OrderSide.Sell && rsiVal >= ob - 10) confirms++; // RSI en zona alta
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
            .Select(kv => string.Create(CultureInfo.InvariantCulture,
                $"{kv.Value.Name}={kv.Value.Calculate():F4}"));

        var snapshot = string.Join(" | ", parts);

        if (side is not null && total > 0)
            snapshot += $" | Confirm={confirms}/{total}";

        if (_lastRegime is not null && _lastRegime.Regime != MarketRegime.Unknown)
            snapshot += $" | Regime={_lastRegime.Regime}";

        return snapshot;
    }
}
