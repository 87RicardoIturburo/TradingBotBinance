using TradingBot.Core.Common;
using TradingBot.Core.Enums;
using TradingBot.Core.Events;
using TradingBot.Core.ValueObjects;

namespace TradingBot.Core.Entities;

/// <summary>
/// Aggregate root principal. Agrupa indicadores, reglas y configuración de riesgo.
/// Es el único punto de entrada para modificar el estado de la estrategia.
/// El hot-reload se realiza a través de <see cref="UpdateConfig"/>.
/// </summary>
public sealed class TradingStrategy : AggregateRoot<Guid>
{
    private List<IndicatorConfig> _indicators = [];
    private List<TradingRule>     _rules      = [];

    public string          Name             { get; private set; } = string.Empty;
    public string?         Description      { get; private set; }
    public Symbol          Symbol           { get; private set; } = null!;
    public StrategyStatus  Status           { get; private set; }
    public TradingMode     Mode             { get; private set; }
    public RiskConfig      RiskConfig       { get; private set; } = null!;
    public DateTimeOffset  CreatedAt        { get; private set; }
    public DateTimeOffset  UpdatedAt        { get; private set; }
    public DateTimeOffset? LastActivatedAt  { get; private set; }

    public IReadOnlyList<IndicatorConfig> Indicators => _indicators.AsReadOnly();
    public IReadOnlyList<TradingRule>     Rules      => _rules.AsReadOnly();

    public bool IsActive => Status == StrategyStatus.Active;

    private TradingStrategy(Guid id) : base(id) { }
    private TradingStrategy() : base(Guid.Empty) { } // EF Core

    // ── Fábrica ───────────────────────────────────────────────────────────

    public static Result<TradingStrategy, DomainError> Create(
        string      name,
        Symbol      symbol,
        TradingMode mode,
        RiskConfig  riskConfig,
        string?     description = null)
    {
        if (string.IsNullOrWhiteSpace(name))
            return Result<TradingStrategy, DomainError>.Failure(
                DomainError.Validation("El nombre de la estrategia no puede estar vacío."));

        if (name.Length > 100)
            return Result<TradingStrategy, DomainError>.Failure(
                DomainError.Validation("El nombre no puede superar 100 caracteres."));

        var now = DateTimeOffset.UtcNow;
        return Result<TradingStrategy, DomainError>.Success(new TradingStrategy(Guid.NewGuid())
        {
            Name        = name.Trim(),
            Description = description?.Trim(),
            Symbol      = symbol,
            Status      = StrategyStatus.Inactive,
            Mode        = mode,
            RiskConfig  = riskConfig,
            CreatedAt   = now,
            UpdatedAt   = now
        });
    }

    // ── Ciclo de vida ─────────────────────────────────────────────────────

    /// <summary>
    /// Activa la estrategia. Requiere al menos una regla habilitada.
    /// </summary>
    public Result<TradingStrategy, DomainError> Activate()
    {
        if (Status == StrategyStatus.Active)
            return Result<TradingStrategy, DomainError>.Failure(
                DomainError.Conflict("La estrategia ya está activa."));

        if (!_rules.Any(r => r.IsEnabled))
            return Result<TradingStrategy, DomainError>.Failure(
                DomainError.InvalidOperation(
                    "La estrategia necesita al menos una regla habilitada para activarse."));

        Status          = StrategyStatus.Active;
        LastActivatedAt = DateTimeOffset.UtcNow;
        UpdatedAt       = LastActivatedAt.Value;
        Version++;

        RaiseDomainEvent(new StrategyActivatedEvent(Id, Name, IsActive: true));
        return Result<TradingStrategy, DomainError>.Success(this);
    }

    public void Deactivate()
    {
        if (Status == StrategyStatus.Inactive) return;
        Status    = StrategyStatus.Inactive;
        UpdatedAt = DateTimeOffset.UtcNow;
        Version++;
        RaiseDomainEvent(new StrategyActivatedEvent(Id, Name, IsActive: false));
    }

    public void Pause()
    {
        if (Status != StrategyStatus.Active) return;
        Status    = StrategyStatus.Paused;
        UpdatedAt = DateTimeOffset.UtcNow;
        Version++;
    }

    /// <summary>El motor detectó un error irrecuperable; marca la estrategia en error.</summary>
    public void MarkAsError()
    {
        Status    = StrategyStatus.Error;
        UpdatedAt = DateTimeOffset.UtcNow;
        Version++;
    }

    // ── Hot-reload ────────────────────────────────────────────────────────

    /// <summary>
    /// Actualiza nombre, descripción y configuración de riesgo en tiempo de ejecución.
    /// Si la estrategia estaba activa, publica <see cref="StrategyUpdatedEvent"/> para
    /// que todos los motores recarguen su estado.
    /// </summary>
    public Result<TradingStrategy, DomainError> UpdateConfig(
        string     name,
        RiskConfig riskConfig,
        string?    description = null)
    {
        if (string.IsNullOrWhiteSpace(name))
            return Result<TradingStrategy, DomainError>.Failure(
                DomainError.Validation("El nombre de la estrategia no puede estar vacío."));

        if (name.Length > 100)
            return Result<TradingStrategy, DomainError>.Failure(
                DomainError.Validation("El nombre no puede superar 100 caracteres."));

        Name        = name.Trim();
        Description = description?.Trim();
        RiskConfig  = riskConfig;
        UpdatedAt   = DateTimeOffset.UtcNow;
        Version++;

        RaiseDomainEvent(new StrategyUpdatedEvent(Id, Name, IsHotReload: IsActive));
        return Result<TradingStrategy, DomainError>.Success(this);
    }

    // ── Indicadores ───────────────────────────────────────────────────────

    public void AddIndicator(IndicatorConfig indicator)
    {
        if (_indicators.Any(i => i.Type == indicator.Type)) return;
        _indicators.Add(indicator);
        UpdatedAt = DateTimeOffset.UtcNow;
        Version++;
    }

    public void RemoveIndicator(IndicatorType type)
    {
        var indicator = _indicators.FirstOrDefault(i => i.Type == type);
        if (indicator is null) return;
        _indicators.Remove(indicator);
        UpdatedAt = DateTimeOffset.UtcNow;
        Version++;
    }

    // ── Reglas ────────────────────────────────────────────────────────────

    public Result<TradingRule, DomainError> AddRule(TradingRule rule)
    {
        if (_rules.Any(r => r.Id == rule.Id))
            return Result<TradingRule, DomainError>.Failure(
                DomainError.Conflict($"La regla '{rule.Id}' ya existe en esta estrategia."));

        _rules.Add(rule);
        UpdatedAt = DateTimeOffset.UtcNow;
        Version++;

        return Result<TradingRule, DomainError>.Success(rule);
    }

    public Result<TradingRule, DomainError> RemoveRule(Guid ruleId)
    {
        var rule = _rules.FirstOrDefault(r => r.Id == ruleId);
        if (rule is null)
            return Result<TradingRule, DomainError>.Failure(
                DomainError.NotFound($"Regla '{ruleId}'"));

        _rules.Remove(rule);
        UpdatedAt = DateTimeOffset.UtcNow;
        Version++;

        return Result<TradingRule, DomainError>.Success(rule);
    }

    public Result<TradingRule, DomainError> GetRule(Guid ruleId)
    {
        var rule = _rules.FirstOrDefault(r => r.Id == ruleId);
        return rule is not null
            ? Result<TradingRule, DomainError>.Success(rule)
            : Result<TradingRule, DomainError>.Failure(DomainError.NotFound($"Regla '{ruleId}'"));
    }
}
