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

        _previousRsi  = null;
        IsInitialized = false;
    }

    private void RebuildIndicators(TradingStrategy config)
    {
        _indicators.Clear();
        _previousRsi = null;

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
        if (!_indicators.TryGetValue(IndicatorType.RSI, out var rsiIndicator) || !rsiIndicator.IsReady)
            return null;

        var rsi   = rsiIndicator.Calculate()!.Value;
        var config = _config!.Indicators
            .FirstOrDefault(i => i.Type == IndicatorType.RSI);

        if (config is null) return null;

        var oversold   = config.GetParameter("oversold", 30);
        var overbought = config.GetParameter("overbought", 70);

        SignalGeneratedEvent? signal = null;

        if (rsi <= oversold && (_previousRsi is null || _previousRsi > oversold))
        {
            signal = new SignalGeneratedEvent(
                StrategyId, Symbol, OrderSide.Buy, tick.LastPrice,
                BuildSnapshot());
        }
        else if (rsi >= overbought && (_previousRsi is null || _previousRsi < overbought))
        {
            signal = new SignalGeneratedEvent(
                StrategyId, Symbol, OrderSide.Sell, tick.LastPrice,
                BuildSnapshot());
        }

        _previousRsi = rsi;
        return signal;
    }

    private string BuildSnapshot()
    {
        var parts = _indicators
            .Where(kv => kv.Value.IsReady)
            .Select(kv => $"{kv.Value.Name}={kv.Value.Calculate():F4}");
        return string.Join(" | ", parts);
    }
}
