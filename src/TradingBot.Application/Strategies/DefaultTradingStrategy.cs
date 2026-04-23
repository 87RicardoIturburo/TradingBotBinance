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
internal class DefaultTradingStrategy : ITradingStrategy
{
    private readonly ILogger<DefaultTradingStrategy> _logger;
    private protected readonly Dictionary<IndicatorType, ITechnicalIndicator> _indicators = [];

    private TradingStrategy? _config;
    private decimal? _previousRsi;
    private decimal? _previousMacdHistogram;
    private decimal? _previousPreviousRsi;
    private decimal? _previousPreviousMacdHistogram;
    private int _previousEmaRelation;
    private int _previousSmaRelation; // -1 = price below SMA, 0 = unknown, 1 = price above SMA
    private DateTimeOffset _lastSignalAt;

    // EST-7: EMA crossover rápida (si está configurada, se usa cruce EMA/EMA en vez de precio/EMA)
    private EmaIndicator? _emaCrossoverFast;

    // EST-2/EST-11: RSI divergencia — historial de mínimos/máximos para detectar divergencias
    private decimal? _rsiLow;
    private decimal? _priceLowAtRsiLow;
    private decimal _lastClosePrice;

    // ── Multi-Timeframe Analysis ────────────────────────────────────────
    /// <summary>EMA del timeframe de confirmación (HTF). Determina tendencia macro.</summary>
    private EmaIndicator? _confirmationEma;
    /// <summary>Período configurado de la EMA de confirmación. Usado en logs.</summary>
    private int _confirmationEmaPeriod;
    private decimal? _lastConfirmationClose;

    // ── EST-15: Filtro de correlación BTC para altcoins ─────────────────
    private EmaIndicator? _btcEma;
    private decimal? _lastBtcClose;
    private bool _isBtcPair;

    // ── EST-17: Re-entrada inteligente tras stop-loss ────────────────────
    private DateTimeOffset _lastStopLossAt;
    private bool _reEntryMode;

    /// <summary>Tiempo mínimo entre señales consecutivas. Se calcula dinámicamente según el timeframe de la estrategia.</summary>
    private TimeSpan _signalCooldown = TimeSpan.FromMinutes(1);

    public Guid        StrategyId    { get; private set; }
    public Symbol      Symbol        { get; private set; } = null!;
    public TradingMode Mode          { get; private set; }
    public bool        IsInitialized { get; private set; }
    public MarketRegime CurrentRegime { get; set; } = MarketRegime.Unknown;
    public bool IsBullish =>
        !_indicators.TryGetValue(IndicatorType.ADX, out var adx)
        || adx is not AdxIndicator { IsReady: true, IsBearish: true };
    public decimal?    CurrentAtrValue =>
        _indicators.TryGetValue(IndicatorType.ATR, out var atrInd) && atrInd.IsReady
            ? atrInd.Calculate() : null;

    public DefaultTradingStrategy(ILogger<DefaultTradingStrategy> logger)
    {
        _logger = logger;
    }

    internal AdxIndicator? GetAdxIndicator() =>
        _indicators.TryGetValue(IndicatorType.ADX, out var raw) ? raw as AdxIndicator : null;

    internal BollingerBandsIndicator? GetBollingerIndicator() =>
        _indicators.TryGetValue(IndicatorType.BollingerBands, out var raw) ? raw as BollingerBandsIndicator : null;

    internal AtrIndicator? GetAtrIndicator() =>
        _indicators.TryGetValue(IndicatorType.ATR, out var raw) ? raw as AtrIndicator : null;

    internal decimal? GetVolumeRatio() =>
        _indicators.TryGetValue(IndicatorType.Volume, out var raw)
        && raw is VolumeSmaIndicator { IsReady: true } vol
            ? vol.VolumeRatio : null;

    public Task InitializeAsync(TradingStrategy config, CancellationToken cancellationToken = default)
    {
        _config    = config;
        StrategyId = config.Id;
        Symbol     = config.Symbol;
        Mode       = config.Mode;

        RebuildIndicators(config);

        // Multi-Timeframe: crear EMA de confirmación con período configurable
        _confirmationEmaPeriod = config.RiskConfig.ConfirmationEmaPeriod;
        _confirmationEma = config.ConfirmationTimeframe.HasValue
            ? new EmaIndicator(_confirmationEmaPeriod)
            : null;
        _lastConfirmationClose = null;

        // EST-15: filtro de correlación BTC para altcoins
        _isBtcPair = config.Symbol.Value.StartsWith("BTC", StringComparison.OrdinalIgnoreCase);
        if (!_isBtcPair)
        {
            _btcEma = new EmaIndicator(config.RiskConfig.ConfirmationEmaPeriod);
            _lastBtcClose = null;
        }
        else
        {
            _btcEma = null;
            _lastBtcClose = null;
        }

        // Cooldown dinámico: porcentaje del intervalo de vela (ej: 50% de 1H = 30min)
        _signalCooldown = CalculateSignalCooldown(config.Timeframe, config.RiskConfig.SignalCooldownPercent);

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

        // TRADE-1 fix: los indicadores solo se alimentan con velas cerradas (ProcessKlineAsync).
        // Los ticks solo se usan para evaluar SL/TP (en StrategyEngine) y notificar al frontend.
        // No generar señales desde ticks — las señales provienen de klines.
        return Task.FromResult(
            Result<SignalGeneratedEvent?, DomainError>.Success((SignalGeneratedEvent?)null));
    }

    public Task<Result<SignalGeneratedEvent?, DomainError>> ProcessKlineAsync(
        KlineClosedEvent kline,
        CancellationToken cancellationToken = default)
    {
        if (!IsInitialized || _config is null)
            return Task.FromResult(
                Result<SignalGeneratedEvent?, DomainError>.Failure(
                    DomainError.InvalidOperation("La estrategia no está inicializada.")));

        // Actualizar indicadores con datos de la vela cerrada.
        // CRIT-1 fix: indicadores OHLC (ATR) reciben datos completos para True Range preciso.
        // EST-6: indicadores de volumen reciben volumen en vez de precio.
        foreach (var indicator in _indicators.Values)
        {
            if (indicator is IVolumeIndicator volInd)
                volInd.UpdateVolume(kline.Volume);
            else if (indicator is IOhlcIndicator ohlc)
                ohlc.UpdateOhlc(kline.High, kline.Low, kline.Close);
            else
                indicator.Update(kline.Close);
        }

        // EST-7: alimentar la EMA crossover rápida
        _emaCrossoverFast?.Update(kline.Close);

        // EST-11: precio de cierre real para RSI divergencia (mode=2)
        _lastClosePrice = kline.Close;

        // Crear un tick sintético a partir de la vela para evaluar la señal
        var lastPrice = Price.Create(kline.Close);
        if (lastPrice.IsFailure)
            return Task.FromResult(
                Result<SignalGeneratedEvent?, DomainError>.Success((SignalGeneratedEvent?)null));

        var bidPrice = Price.Create(kline.Low);
        var askPrice = Price.Create(kline.High);

        var syntheticTick = new MarketTickReceivedEvent(
            kline.Symbol,
            bidPrice.IsSuccess ? bidPrice.Value : lastPrice.Value,
            askPrice.IsSuccess ? askPrice.Value : lastPrice.Value,
            lastPrice.Value,
            kline.Volume,
            kline.CloseTime);

        var signal = EvaluateSignal(syntheticTick);

        return Task.FromResult(
            Result<SignalGeneratedEvent?, DomainError>.Success(signal));
    }

    public Task ReloadConfigAsync(TradingStrategy config, CancellationToken cancellationToken = default)
    {
        _config = config;
        Mode    = config.Mode;
        RebuildIndicators(config);

        // Reconstruir confirmación si cambió
        _confirmationEmaPeriod = config.RiskConfig.ConfirmationEmaPeriod;
        _confirmationEma = config.ConfirmationTimeframe.HasValue
            ? new EmaIndicator(_confirmationEmaPeriod)
            : null;
        _lastConfirmationClose = null;

        // EST-15: reconstruir filtro BTC si cambió el símbolo
        _isBtcPair = config.Symbol.Value.StartsWith("BTC", StringComparison.OrdinalIgnoreCase);
        if (!_isBtcPair)
        {
            _btcEma = new EmaIndicator(config.RiskConfig.ConfirmationEmaPeriod);
            _lastBtcClose = null;
        }
        else
        {
            _btcEma = null;
            _lastBtcClose = null;
        }

        // Recalcular cooldown
        _signalCooldown = CalculateSignalCooldown(config.Timeframe, config.RiskConfig.SignalCooldownPercent);

        _logger.LogInformation(
            "Hot-reload completado para estrategia '{Name}' ({Id})", config.Name, config.Id);

        return Task.CompletedTask;
    }

    // ── Multi-Timeframe Analysis ────────────────────────────────────────

    public void ProcessConfirmationKline(KlineClosedEvent kline)
    {
        if (_confirmationEma is null) return;

        _confirmationEma.Update(kline.Close);
        _lastConfirmationClose = kline.Close;

        if (_confirmationEma.IsReady)
        {
            var emaVal = _confirmationEma.Calculate()!.Value;
            var trend = kline.Close > emaVal ? "ALCISTA" : "BAJISTA";
            _logger.LogDebug(
                "HTF EMA({Period})={Ema:F2} Close={Close:F2} → Tendencia {Trend}",
                _confirmationEmaPeriod, emaVal, kline.Close, trend);
        }
    }

    public bool IsConfirmationAligned(OrderSide side)
    {
        // Sin confirmación configurada → siempre alineado
        if (_confirmationEma is null || !_confirmationEma.IsReady || _lastConfirmationClose is null)
            return true;

        var emaValue = _confirmationEma.Calculate()!.Value;

        return side switch
        {
            OrderSide.Buy  => _lastConfirmationClose.Value >= emaValue, // Tendencia alcista
            OrderSide.Sell => _lastConfirmationClose.Value <= emaValue, // Tendencia bajista
            _ => true
        };
    }

    // ── EST-15: Filtro de correlación BTC ────────────────────────────────

    public void ProcessBtcKline(KlineClosedEvent kline)
    {
        if (_btcEma is null) return;

        _btcEma.Update(kline.Close);
        _lastBtcClose = kline.Close;

        if (_btcEma.IsReady)
        {
            var emaVal = _btcEma.Calculate()!.Value;
            var trend = kline.Close > emaVal ? "ALCISTA" : "BAJISTA";
            _logger.LogDebug(
                "BTC EMA({Period})={Ema:F2} Close={Close:F2} → Tendencia BTC {Trend}",
                _confirmationEmaPeriod, emaVal, kline.Close, trend);
        }
    }

    public bool IsBtcAligned(OrderSide side)
    {
        if (_isBtcPair || _btcEma is null || !_btcEma.IsReady || _lastBtcClose is null)
            return true;

        var btcEmaValue = _btcEma.Calculate()!.Value;

        return side switch
        {
            OrderSide.Buy  => _lastBtcClose.Value >= btcEmaValue,
            OrderSide.Sell => _lastBtcClose.Value <= btcEmaValue,
            _ => true
        };
    }

    public void NotifyStopLossHit()
    {
        _lastStopLossAt = DateTimeOffset.UtcNow;
        _reEntryMode = IsTrendIntact();

        if (_reEntryMode)
        {
            _logger.LogInformation(
                "EST-17: SL activado pero tendencia intacta — modo re-entrada activo (cooldown reducido a 25%)");
        }
    }

    public void Reset()
    {
        foreach (var indicator in _indicators.Values)
            indicator.Reset();

        _previousRsi                    = null;
        _previousPreviousRsi            = null;
        _previousMacdHistogram          = null;
        _previousPreviousMacdHistogram  = null;
        _previousEmaRelation            = 0;
        _lastSignalAt                   = default;
        _confirmationEma?.Reset();
        _lastConfirmationClose = null;
        _btcEma?.Reset();
        _lastBtcClose          = null;
        _emaCrossoverFast?.Reset();
        _rsiLow                = null;
        _priceLowAtRsiLow      = null;
        _lastClosePrice        = 0m;
        _lastStopLossAt        = default;
        _reEntryMode           = false;
        IsInitialized          = false;
    }

    public void WarmUpPrice(decimal price)
    {
        foreach (var indicator in _indicators.Values)
            indicator.Update(price);
        _emaCrossoverFast?.Update(price);
        _lastClosePrice = price;
    }

    public void WarmUpOhlc(decimal high, decimal low, decimal close, decimal volume = 0m)
    {
        foreach (var indicator in _indicators.Values)
        {
            if (indicator is IVolumeIndicator volInd)
                volInd.UpdateVolume(volume);
            else if (indicator is IOhlcIndicator ohlc)
                ohlc.UpdateOhlc(high, low, close);
            else
                indicator.Update(close);
        }
        _emaCrossoverFast?.Update(close);
        _lastClosePrice = close;
    }

    public string GetCurrentSnapshot() => BuildSnapshot();

    /// <summary>
    /// Serializa el estado de todos los indicadores para persistir en Redis.
    /// </summary>
    internal IReadOnlyDictionary<IndicatorType, string> SaveIndicatorStates()
    {
        var states = new Dictionary<IndicatorType, string>();
        foreach (var (type, indicator) in _indicators)
        {
            if (indicator.IsReady)
                states[type] = indicator.SerializeState();
        }
        return states;
    }

    /// <summary>
    /// Restaura el estado de los indicadores desde datos persistidos en Redis.
    /// Devuelve <c>true</c> si todos los indicadores se restauraron correctamente.
    /// </summary>
    internal bool RestoreIndicatorStates(IReadOnlyDictionary<IndicatorType, string> savedStates)
    {
        var allRestored = true;
        foreach (var (type, indicator) in _indicators)
        {
            if (!savedStates.TryGetValue(type, out var json))
            {
                allRestored = false;
                continue;
            }

            if (!indicator.DeserializeState(json))
            {
                allRestored = false;
                _logger.LogWarning(
                    "No se pudo restaurar el estado del indicador {Type} — se necesita warm-up",
                    type);
            }
        }

        if (!allRestored)
            return false;

        // Sincronizar estado previo para evitar señales falsas
        SyncPreviousIndicatorState();
        return true;
    }

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
        _previousRsi                    = null;
        _previousPreviousRsi            = null;
        _previousMacdHistogram          = null;
        _previousPreviousMacdHistogram  = null;
        _previousEmaRelation            = 0;
        _previousSmaRelation   = 0;
        _lastSignalAt          = default;
        _emaCrossoverFast      = null;
        _signalCooldown        = CalculateSignalCooldown(config.Timeframe, config.RiskConfig.SignalCooldownPercent);

        foreach (var indicatorConfig in config.Indicators)
        {
            try
            {
                var indicator = IndicatorFactory.Create(indicatorConfig);
                _indicators[indicatorConfig.Type] = indicator;

                // EST-7: si la EMA tiene crossoverPeriod, crear EMA rápida para cruce EMA/EMA
                if (indicatorConfig.Type == IndicatorType.EMA)
                {
                    var crossoverPeriod = (int)indicatorConfig.GetParameter("crossoverPeriod", 0);
                    if (crossoverPeriod >= 2)
                        _emaCrossoverFast = new EmaIndicator(crossoverPeriod);
                }
            }
            catch (NotSupportedException ex)
            {
                _logger.LogWarning(ex, "Indicador no soportado: {Type}", indicatorConfig.Type);
            }
        }
    }

    /// <summary>
    /// Calcula el cooldown entre señales como porcentaje de la duración de una vela.
    /// Ej: 50% de 1H = 30 minutos. Si el porcentaje es 0 se usa un mínimo de 5 segundos.
    /// </summary>
    private static TimeSpan CalculateSignalCooldown(CandleInterval timeframe, decimal cooldownPercent)
    {
        var intervalMinutes = timeframe.ToMinutes();

        if (cooldownPercent <= 0)
            return TimeSpan.FromSeconds(5);

        var cooldownMinutes = intervalMinutes * (double)(cooldownPercent / 100m);
        return TimeSpan.FromMinutes(Math.Max(cooldownMinutes, 5.0 / 60.0)); // Mínimo 5 segundos
    }

    private SignalGeneratedEvent? EvaluateSignal(MarketTickReceivedEvent tick)
    {
        var price = tick.LastPrice.Value;

        if (CurrentRegime is MarketRegime.HighVolatility or MarketRegime.Bearish or MarketRegime.Indefinite)
        {
            UpdatePreviousIndicatorState(price);
            return null;
        }

        // Determinar señal candidata — el primer generador disponible decide, los demás confirman
        var (candidateSide, signalSource, signalNature) = DetermineSignalCandidate(price);

        if (candidateSide is null)
        {
            UpdatePreviousIndicatorState(price);
            return null;
        }

        // Trending → solo operar en la dirección de la tendencia (ADX + DI)
        var adx = GetAdxIndicator();
        if (CurrentRegime == MarketRegime.Trending && adx is { IsReady: true })
        {
            if (candidateSide == OrderSide.Buy && adx.IsBearish)
            {
                _logger.LogDebug(
                    "Señal Buy descartada: tendencia bajista (+DI={PlusDi:F1}, -DI={MinusDi:F1})",
                    adx.PlusDi, adx.MinusDi);
                UpdatePreviousIndicatorState(price);
                return null;
            }
            if (candidateSide == OrderSide.Sell && adx.IsBullish)
            {
                _logger.LogDebug(
                    "Señal Sell descartada: tendencia alcista (+DI={PlusDi:F1}, -DI={MinusDi:F1})",
                    adx.PlusDi, adx.MinusDi);
                UpdatePreviousIndicatorState(price);
                return null;
            }
        }

        // Cooldown: evitar ráfagas de señales en períodos volátiles
        // EST-17: en modo re-entrada post-SL, reducir cooldown a 25%
        var effectiveCooldown = _reEntryMode ? _signalCooldown * 0.25 : _signalCooldown;
        if (_lastSignalAt != default && tick.Timestamp - _lastSignalAt < effectiveCooldown)
            return null;

        // EST-17: desactivar re-entry mode una vez que se genera una señal
        if (_reEntryMode)
            _reEntryMode = false;

        // Confirmación multi-indicador: indicadores que NO son el generador emiten un voto
        var (confirms, total) = CountConfirmations(candidateSide.Value, price, signalSource, signalNature);

        if (total > 0)
        {
            var requiredPercent = _config!.RiskConfig.MinConfirmationPercent / 100m;
            var requiredConfirms = (int)Math.Ceiling(total * requiredPercent);
            if (confirms < requiredConfirms)
            {
                _logger.LogDebug(
                    "Señal {Source} {Side} descartada: confirmación {Confirms}/{Total} (req: {Required}, {Percent}%)",
                    signalSource, candidateSide, confirms, total, requiredConfirms, _config.RiskConfig.MinConfirmationPercent);
                return null;
            }
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
    /// Determina la señal candidata seleccionando el generador primario según el régimen de mercado.
    /// <list type="bullet">
    ///   <item><b>Trending</b> → MACD/EMA/SMA primero (momentum y dirección)</item>
    ///   <item><b>Ranging</b>  → RSI/Bollinger primero (reversión a la media)</item>
    ///   <item><b>Unknown</b>  → prioridad clásica RSI → MACD → BB → EMA → SMA</item>
    /// </list>
    /// </summary>
    private protected virtual (OrderSide? Side, IndicatorType Source, SignalNature Nature) DetermineSignalCandidate(decimal price)
    {
        return CurrentRegime switch
        {
            MarketRegime.Trending => TryTrendingGenerators(price),
            MarketRegime.Ranging  => TryRangingGenerators(price),
            _                     => TryDefaultGenerators(price)
        };
    }

    private protected (OrderSide? Side, IndicatorType Source, SignalNature Nature) TryTrendingGenerators(decimal price)
    {
        var result = TryMacdSignal();
        if (result.Side is not null) return result;

        result = TryEmaSignal(price);
        if (result.Side is not null) return result;

        result = TrySmaSignal(price);
        if (result.Side is not null) return result;

        result = TryRsiSignal();
        if (result.Side is not null) return result;

        return (null, default, default);
    }

    private protected (OrderSide? Side, IndicatorType Source, SignalNature Nature) TryRangingGenerators(decimal price)
    {
        var result = TryRsiSignal();
        if (result.Side is not null) return result;

        result = TryBollingerSignal(price);
        if (result.Side is not null) return result;

        result = TryMacdSignal();
        if (result.Side is not null) return result;

        result = TryEmaSignal(price);
        if (result.Side is not null) return result;

        result = TrySmaSignal(price);
        if (result.Side is not null) return result;

        return (null, default, default);
    }

    private protected (OrderSide? Side, IndicatorType Source, SignalNature Nature) TryDefaultGenerators(decimal price)
    {
        var result = TryRsiSignal();
        if (result.Side is not null) return result;

        result = TryMacdSignal();
        if (result.Side is not null) return result;

        result = TryBollingerSignal(price);
        if (result.Side is not null) return result;

        result = TryEmaSignal(price);
        if (result.Side is not null) return result;

        result = TrySmaSignal(price);
        if (result.Side is not null) return result;

        return (null, default, default);
    }

    private protected (OrderSide? Side, IndicatorType Source, SignalNature Nature) TryRsiSignal()
    {
        if (!_indicators.TryGetValue(IndicatorType.RSI, out var rsiIndicator) || !rsiIndicator.IsReady)
            return (null, default, default);

        var rsi = rsiIndicator.Calculate()!.Value;
        var config = _config!.Indicators.FirstOrDefault(i => i.Type == IndicatorType.RSI);
        if (config is null)
        {
            _previousRsi = rsiIndicator.Calculate();
            return (null, default, default);
        }

        var oversold   = config.GetParameter("oversold", 30);
        var overbought = config.GetParameter("overbought", 70);
        var rsiMode    = (int)config.GetParameter("mode", 0);

        OrderSide? rsiSide = null;

        switch (rsiMode)
        {
            // Modo 0 (conservador): señal al cruzar de vuelta la zona extrema
            case 0:
            default:
                if (rsi > oversold && _previousRsi is not null && _previousRsi.Value <= oversold)
                    rsiSide = OrderSide.Buy;
                else if (rsi < overbought && _previousRsi is not null && _previousRsi.Value >= overbought)
                    rsiSide = OrderSide.Sell;
                break;

            // Modo 1 (agresivo): señal al entrar en zona extrema
            case 1:
                if (rsi <= oversold && (_previousRsi is null || _previousRsi.Value > oversold))
                    rsiSide = OrderSide.Buy;
                else if (rsi >= overbought && (_previousRsi is null || _previousRsi.Value < overbought))
                    rsiSide = OrderSide.Sell;
                break;

            // Modo 2 (divergencia bullish): RSI hace higher low mientras precio hace lower low
            case 2:
            {
                // EST-11 fix: usar precio de cierre real en vez de EMA como proxy
                var currentPrice = _lastClosePrice;

                if (rsi <= oversold + 5m)
                {
                    if (_rsiLow is null || rsi < _rsiLow.Value)
                    {
                        _rsiLow = rsi;
                        _priceLowAtRsiLow = currentPrice;
                    }
                }

                if (_rsiLow is not null && _priceLowAtRsiLow is not null && rsi > oversold)
                {
                    if (rsi > _rsiLow.Value && currentPrice > 0 && currentPrice < _priceLowAtRsiLow.Value)
                    {
                        rsiSide = OrderSide.Buy;
                        _rsiLow = null;
                        _priceLowAtRsiLow = null;
                    }
                    else if (rsi > oversold + 10m)
                    {
                        _rsiLow = null;
                        _priceLowAtRsiLow = null;
                    }
                }
                break;
            }

            // Modo 3 (early reversal): señal cuando RSI forma pendiente positiva
            // por 2 velas consecutivas mientras sigue en zona oversold.
            // Reduce lag ~2-3 velas vs modo 0.
            case 3:
            {
                if (_previousRsi is not null && _previousPreviousRsi is not null
                    && rsi < oversold + 5m
                    && rsi > _previousRsi.Value
                    && _previousRsi.Value > _previousPreviousRsi.Value)
                {
                    rsiSide = OrderSide.Buy;
                }
                else if (_previousRsi is not null && _previousPreviousRsi is not null
                    && rsi > overbought - 5m
                    && rsi < _previousRsi.Value
                    && _previousRsi.Value < _previousPreviousRsi.Value)
                {
                    rsiSide = OrderSide.Sell;
                }
                break;
            }
        }

        _previousPreviousRsi = _previousRsi;
        _previousRsi = rsi;

        return rsiSide is not null ? (rsiSide, IndicatorType.RSI, SignalNature.MeanReversion) : (null, default, default);
    }

    private protected (OrderSide? Side, IndicatorType Source, SignalNature Nature) TryMacdSignal()
    {
        if (!_indicators.TryGetValue(IndicatorType.MACD, out var macdRaw)
            || macdRaw is not MacdIndicator macd || !macd.IsReady)
            return (null, default, default);

        var histogram = macd.Histogram ?? 0m;
        var macdConfig = _config!.Indicators.FirstOrDefault(i => i.Type == IndicatorType.MACD);
        var histogramSlopeMode = (int)(macdConfig?.GetParameter("histogramSlopeMode", 0) ?? 0);

        OrderSide? macdSide = null;

        if (_previousMacdHistogram is not null)
        {
            // Modo clásico: cruce de cero del histograma
            if (histogram > 0m && _previousMacdHistogram.Value <= 0m)
                macdSide = OrderSide.Buy;
            else if (histogram < 0m && _previousMacdHistogram.Value >= 0m)
                macdSide = OrderSide.Sell;

            // histogramSlopeMode=1: señal adicional en cambio de pendiente del histograma.
            // Detecta cuando el momentum deja de empeorar (histograma gira)
            // sin esperar cruce de cero. Reduce lag ~2-5 velas.
            if (macdSide is null && histogramSlopeMode == 1 && _previousPreviousMacdHistogram is not null)
            {
                var prevSlope = _previousMacdHistogram.Value - _previousPreviousMacdHistogram.Value;
                var currSlope = histogram - _previousMacdHistogram.Value;

                if (histogram < 0m && prevSlope < 0m && currSlope > 0m)
                    macdSide = OrderSide.Buy;
                else if (histogram > 0m && prevSlope > 0m && currSlope < 0m)
                    macdSide = OrderSide.Sell;
            }
        }

        _previousPreviousMacdHistogram = _previousMacdHistogram;
        _previousMacdHistogram = histogram;

        if (macdSide is null)
            return (null, default, default);

        // EST-5: filtro de fuerza del histograma — descartar cruces débiles
        var minHistogramStrength = macdConfig?.GetParameter("minHistogramStrength", 0m) ?? 0m;
        if (minHistogramStrength > 0 && Math.Abs(histogram) < minHistogramStrength)
        {
            _logger.LogDebug(
                "MACD {Side} descartada: histograma {Hist:F6} < umbral mínimo {Min:F6}",
                macdSide, histogram, minHistogramStrength);
            return (null, default, default);
        }

        return (macdSide, IndicatorType.MACD, SignalNature.TrendFollowing);
    }

    private protected (OrderSide? Side, IndicatorType Source, SignalNature Nature) TryBollingerSignal(decimal price)
    {
        if (!_indicators.TryGetValue(IndicatorType.BollingerBands, out var bbIndicator)
            || bbIndicator is not BollingerBandsIndicator bbGen || !bbGen.IsReady)
            return (null, default, default);

        // BB mean-reversion: solo en mercado lateral (Ranging/Unknown)
        if (CurrentRegime is not MarketRegime.Trending)
        {
            if (price <= bbGen.LowerBand!.Value)
                return (OrderSide.Buy, IndicatorType.BollingerBands, SignalNature.MeanReversion);
            if (price >= bbGen.UpperBand!.Value)
                return (OrderSide.Sell, IndicatorType.BollingerBands, SignalNature.MeanReversion);
        }

        // EST-3/EST-14: Bollinger Squeeze → breakout alcista (Spot: solo Buy)
        // SqueezeReleased detecta la vela exacta; WasSqueezeReleasedRecently
        // captura breakouts que tardan hasta 3 velas en desarrollarse.
        if ((bbGen.SqueezeReleased || bbGen.WasSqueezeReleasedRecently(3))
            && price > bbGen.UpperBand!.Value)
            return (OrderSide.Buy, IndicatorType.BollingerBands, SignalNature.TrendFollowing);

        return (null, default, default);
    }

    private protected (OrderSide? Side, IndicatorType Source, SignalNature Nature) TryEmaSignal(decimal price)
    {
        if (!_indicators.TryGetValue(IndicatorType.EMA, out var emaIndicator) || !emaIndicator.IsReady)
            return (null, default, default);

        var emaValue = emaIndicator.Calculate()!.Value;

        // EST-7: cruce EMA rápida / EMA lenta (Golden Cross / Death Cross)
        if (_emaCrossoverFast is { IsReady: true })
        {
            var fastEma = _emaCrossoverFast.Calculate()!.Value;
            var currentRelation = fastEma >= emaValue ? 1 : -1;

            if (currentRelation == 1 && _previousEmaRelation == -1)
            {
                _previousEmaRelation = 1;
                return (OrderSide.Buy, IndicatorType.EMA, SignalNature.TrendFollowing);
            }
            if (currentRelation == -1 && _previousEmaRelation == 1)
            {
                _previousEmaRelation = -1;
                return (OrderSide.Sell, IndicatorType.EMA, SignalNature.TrendFollowing);
            }
            _previousEmaRelation = currentRelation;
            return (null, default, default);
        }

        // Fallback: cruce precio/EMA (comportamiento original)
        if (price > emaValue && _previousEmaRelation == -1)
        {
            _previousEmaRelation = 1;
            return (OrderSide.Buy, IndicatorType.EMA, SignalNature.TrendFollowing);
        }
        if (price < emaValue && _previousEmaRelation == 1)
        {
            _previousEmaRelation = -1;
            return (OrderSide.Sell, IndicatorType.EMA, SignalNature.TrendFollowing);
        }
        _previousEmaRelation = price >= emaValue ? 1 : -1;

        return (null, default, default);
    }

    private protected (OrderSide? Side, IndicatorType Source, SignalNature Nature) TrySmaSignal(decimal price)
    {
        if (!_indicators.TryGetValue(IndicatorType.SMA, out var smaIndicator) || !smaIndicator.IsReady)
            return (null, default, default);

        var smaValue = smaIndicator.Calculate()!.Value;
        if (price > smaValue && _previousSmaRelation == -1)
        {
            _previousSmaRelation = 1;
            return (OrderSide.Buy, IndicatorType.SMA, SignalNature.TrendFollowing);
        }
        if (price < smaValue && _previousSmaRelation == 1)
        {
            _previousSmaRelation = -1;
            return (OrderSide.Sell, IndicatorType.SMA, SignalNature.TrendFollowing);
        }
        _previousSmaRelation = price >= smaValue ? 1 : -1;

        return (null, default, default);
    }

    /// <summary>Actualiza el estado previo de indicadores cuando no se genera señal.</summary>
    private void UpdatePreviousIndicatorState(decimal currentPrice)
    {
        if (_indicators.TryGetValue(IndicatorType.RSI, out var rsi) && rsi.IsReady)
            _previousRsi = rsi.Calculate();

        if (_indicators.TryGetValue(IndicatorType.MACD, out var macdRaw)
            && macdRaw is MacdIndicator macd && macd.IsReady)
            _previousMacdHistogram = macd.Histogram;

        if (_indicators.TryGetValue(IndicatorType.EMA, out var ema) && ema.IsReady)
        {
            var emaValue = ema.Calculate()!.Value;
            if (_emaCrossoverFast is { IsReady: true })
            {
                var fastEma = _emaCrossoverFast.Calculate()!.Value;
                _previousEmaRelation = fastEma >= emaValue ? 1 : -1;
            }
            else
            {
                _previousEmaRelation = currentPrice >= emaValue ? 1 : -1;
            }
        }

        if (_indicators.TryGetValue(IndicatorType.SMA, out var sma) && sma.IsReady)
        {
            var smaValue = sma.Calculate()!.Value;
            _previousSmaRelation = currentPrice >= smaValue ? 1 : -1;
        }
    }

    /// <summary>
    /// Cuenta cuántos indicadores adicionales confirman la señal candidata.
    /// Devuelve (confirmaciones, total_votantes). El indicador que generó la señal se excluye.
    /// Indicadores no listos se excluyen.
    /// </summary>
    internal (int Confirms, int Total) CountConfirmations(
        OrderSide side, decimal price,
        IndicatorType signalSource = default,
        SignalNature signalNature = SignalNature.TrendFollowing)
    {
        var confirms = 0;
        var total    = 0;

        // MACD como confirmador:
        // TrendFollowing → histograma positivo/negativo (momentum claro)
        // MeanReversion  → histograma girando (cambio de pendiente), no exigir cruce de cero
        if (signalSource != IndicatorType.MACD
            && _indicators.TryGetValue(IndicatorType.MACD, out var macdRaw)
            && macdRaw is MacdIndicator macd && macd.IsReady)
        {
            total++;
            var histogram = macd.Histogram ?? 0m;

            if (signalNature == SignalNature.MeanReversion)
            {
                if (side == OrderSide.Buy && _previousMacdHistogram is not null && histogram > _previousMacdHistogram.Value) confirms++;
                else if (side == OrderSide.Sell && _previousMacdHistogram is not null && histogram < _previousMacdHistogram.Value) confirms++;
            }
            else
            {
                if (side == OrderSide.Buy && histogram > 0m) confirms++;
                else if (side == OrderSide.Sell && histogram < 0m) confirms++;
            }
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

        // EMA como confirmador:
        // TrendFollowing → price > EMA = tendencia alcista
        // MeanReversion  → price < EMA = precio en zona baja (consistente con oversold)
        if (signalSource != IndicatorType.EMA
            && _indicators.TryGetValue(IndicatorType.EMA, out var ema) && ema.IsReady)
        {
            total++;
            var emaValue = ema.Calculate()!.Value;
            if (signalNature == SignalNature.MeanReversion)
            {
                if (side == OrderSide.Buy && price < emaValue) confirms++;
                else if (side == OrderSide.Sell && price > emaValue) confirms++;
            }
            else
            {
                if (side == OrderSide.Buy && price > emaValue) confirms++;
                else if (side == OrderSide.Sell && price < emaValue) confirms++;
            }
        }

        // SMA como confirmador:
        // TrendFollowing → price > SMA = tendencia alcista
        // MeanReversion  → price < SMA = precio en zona baja (consistente con oversold)
        if (signalSource != IndicatorType.SMA
            && _indicators.TryGetValue(IndicatorType.SMA, out var sma) && sma.IsReady)
        {
            total++;
            var smaValue = sma.Calculate()!.Value;
            if (signalNature == SignalNature.MeanReversion)
            {
                if (side == OrderSide.Buy && price < smaValue) confirms++;
                else if (side == OrderSide.Sell && price > smaValue) confirms++;
            }
            else
            {
                if (side == OrderSide.Buy && price > smaValue) confirms++;
                else if (side == OrderSide.Sell && price < smaValue) confirms++;
            }
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
            var confirmationZone = rsiConfig?.GetParameter("confirmationZone", 10) ?? 10;
            if (side == OrderSide.Buy && rsiVal <= os + confirmationZone) confirms++;
            else if (side == OrderSide.Sell && rsiVal >= ob - confirmationZone) confirms++;
        }

        // EST-9: Linear Regression con default minRSquared = 0.7 (antes 0.5)
        if (_indicators.TryGetValue(IndicatorType.LinearRegression, out var lrRaw)
            && lrRaw is LinearRegressionIndicator lr && lr.IsReady)
        {
            total++;
            var slope    = lr.Slope ?? 0m;
            var rSquared = lr.RSquared ?? 0m;
            var lrConfig = _config!.Indicators.FirstOrDefault(i => i.Type == IndicatorType.LinearRegression);
            var minRSquared = lrConfig?.GetParameter("minRSquared", 0.7m) ?? 0.7m;

            if (rSquared > minRSquared)
            {
                if (side == OrderSide.Buy && slope > 0m) confirms++;
                else if (side == OrderSide.Sell && slope < 0m) confirms++;
            }
        }

        // EST-8: Fibonacci contextual según régimen
        if (_indicators.TryGetValue(IndicatorType.Fibonacci, out var fibRaw)
            && fibRaw is FibonacciIndicator fib && fib.IsReady)
        {
            total++;
            var nearLevel = fib.GetNearestLevel(price);

            if (nearLevel is not null)
            {
                if (side == OrderSide.Buy)
                {
                    // Trending: pullback superficial (0.382-0.5) indica tendencia fuerte
                    // Ranging: retroceso profundo (0.618-0.786) indica soporte fuerte
                    if (CurrentRegime == MarketRegime.Trending && nearLevel >= 0.382m && nearLevel <= 0.5m)
                        confirms++;
                    else if (nearLevel >= 0.618m)
                        confirms++;
                }
                else if (side == OrderSide.Sell)
                {
                    if (CurrentRegime == MarketRegime.Trending && nearLevel <= 0.5m && nearLevel >= 0.382m)
                        confirms++;
                    else if (nearLevel <= 0.382m)
                        confirms++;
                }
            }
        }

        // EST-6: Volume SMA como confirmador — volumen alto valida la señal
        if (_indicators.TryGetValue(IndicatorType.Volume, out var volRaw)
            && volRaw is VolumeSmaIndicator volSma && volSma.IsReady)
        {
            total++;
            var volConfig = _config!.Indicators.FirstOrDefault(i => i.Type == IndicatorType.Volume);
            var minRatio = volConfig?.GetParameter("minRatio", 1.5m) ?? 1.5m;
            var ratio = volSma.VolumeRatio;
            if (ratio is not null && ratio.Value >= minRatio)
                confirms++;
        }

        return (confirms, total);
    }

    /// <summary>
    /// EST-17: Verifica si la tendencia sigue intacta tras un stop-loss.
    /// Comprueba ADX (tendencia fuerte), EMA (precio sobre media), y MACD (histograma positivo).
    /// Si al menos 2 de 3 indicadores confirman, la tendencia se considera intacta.
    /// </summary>
    private bool IsTrendIntact()
    {
        var confirmations = 0;
        var available = 0;

        if (_indicators.TryGetValue(IndicatorType.ADX, out var adxRaw)
            && adxRaw is AdxIndicator { IsReady: true } adxInd)
        {
            available++;
            if (adxInd.Adx >= (_config?.RiskConfig.AdxTrendingThreshold ?? 25m) && adxInd.IsBullish)
                confirmations++;
        }

        if (_indicators.TryGetValue(IndicatorType.EMA, out var emaRaw) && emaRaw.IsReady)
        {
            available++;
            var emaValue = emaRaw.Calculate()!.Value;
            if (_lastClosePrice > emaValue)
                confirmations++;
        }

        if (_indicators.TryGetValue(IndicatorType.MACD, out var macdRaw)
            && macdRaw is MacdIndicator { IsReady: true } macdInd)
        {
            available++;
            if (macdInd.Histogram is > 0)
                confirmations++;
        }

        return available >= 2 && confirmations >= 2;
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

        if (CurrentRegime != MarketRegime.Unknown)
            snapshot += $" | Regime={CurrentRegime}";

        return snapshot;
    }
}
