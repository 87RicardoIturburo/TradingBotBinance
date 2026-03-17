using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using TradingBot.API.Dtos;
using TradingBot.API.Middleware;
using TradingBot.Application.Commands.Strategies;
using TradingBot.Application.Queries.Strategies;

namespace TradingBot.API.Controllers;

[ApiController]
[Authorize]
[Route("api/[controller]")]
public sealed class StrategiesController(ISender mediator) : ControllerBase
{
    /// <summary>Lista todas las estrategias.</summary>
    [HttpGet]
    public async Task<IResult> GetAll(CancellationToken ct)
    {
        var strategies = await mediator.Send(new GetAllStrategiesQuery(), ct);
        return Results.Ok(strategies.Select(StrategyDto.FromDomain));
    }

    /// <summary>Obtiene una estrategia por Id.</summary>
    [HttpGet("{id:guid}")]
    public async Task<IResult> GetById(Guid id, CancellationToken ct)
    {
        var result = await mediator.Send(new GetStrategyByIdQuery(id), ct);
        return result.ToHttpResult(StrategyDto.FromDomain);
    }

    /// <summary>Crea una nueva estrategia.</summary>
    [HttpPost]
    public async Task<IResult> Create([FromBody] CreateStrategyRequest request, CancellationToken ct)
    {
        var result = await mediator.Send(new CreateStrategyCommand(
            request.Name, request.Symbol, request.Mode,
            request.MaxOrderAmountUsdt, request.MaxDailyLossUsdt,
            request.StopLossPercent, request.TakeProfitPercent,
            request.MaxOpenPositions, request.Description,
            request.UseAtrSizing, request.RiskPercentPerTrade, request.AtrMultiplier,
            request.Timeframe, request.ConfirmationTimeframe), ct);

        return result.ToHttpResult(StrategyDto.FromDomain, StatusCodes.Status201Created);
    }

    /// <summary>Actualiza una estrategia existente (hot-reload si activa).</summary>
    [HttpPut("{id:guid}")]
    public async Task<IResult> Update(Guid id, [FromBody] UpdateStrategyRequest request, CancellationToken ct)
    {
        var result = await mediator.Send(new UpdateStrategyCommand(
            id, request.Name, request.Symbol, request.Mode,
            request.MaxOrderAmountUsdt, request.MaxDailyLossUsdt,
            request.StopLossPercent, request.TakeProfitPercent,
            request.MaxOpenPositions, request.Description,
            request.UseAtrSizing, request.RiskPercentPerTrade, request.AtrMultiplier), ct);

        return result.ToHttpResult(StrategyDto.FromDomain);
    }

    /// <summary>Duplica una estrategia con todos sus indicadores y reglas.</summary>
    [HttpPost("{id:guid}/duplicate")]
    public async Task<IResult> Duplicate(Guid id, CancellationToken ct)
    {
        var result = await mediator.Send(new DuplicateStrategyCommand(id), ct);
        return result.ToHttpResult(StrategyDto.FromDomain, StatusCodes.Status201Created);
    }

    /// <summary>Elimina una estrategia inactiva.</summary>
    [HttpDelete("{id:guid}")]
    public async Task<IResult> Delete(Guid id, CancellationToken ct)
    {
        var result = await mediator.Send(new DeleteStrategyCommand(id), ct);
        return result.ToHttpResult();
    }

    /// <summary>Activa una estrategia.</summary>
    [HttpPost("{id:guid}/activate")]
    public async Task<IResult> Activate(Guid id, CancellationToken ct)
    {
        var result = await mediator.Send(new ActivateStrategyCommand(id), ct);
        return result.ToHttpResult(StrategyDto.FromDomain);
    }

    /// <summary>Desactiva una estrategia.</summary>
    [HttpPost("{id:guid}/deactivate")]
    public async Task<IResult> Deactivate(Guid id, CancellationToken ct)
    {
        var result = await mediator.Send(new DeactivateStrategyCommand(id), ct);
        return result.ToHttpResult(StrategyDto.FromDomain);
    }

    /// <summary>Agrega un indicador técnico a la estrategia.</summary>
    [HttpPost("{id:guid}/indicators")]
    public async Task<IResult> AddIndicator(Guid id, [FromBody] AddIndicatorRequest request, CancellationToken ct)
    {
        var result = await mediator.Send(new AddIndicatorCommand(id, request.Type, request.Parameters), ct);
        return result.ToHttpResult(StrategyDto.FromDomain);
    }

    /// <summary>Actualiza los parámetros de un indicador existente.</summary>
    [HttpPut("{id:guid}/indicators")]
    public async Task<IResult> UpdateIndicator(Guid id, [FromBody] AddIndicatorRequest request, CancellationToken ct)
    {
        var result = await mediator.Send(new UpdateIndicatorCommand(id, request.Type, request.Parameters), ct);
        return result.ToHttpResult(StrategyDto.FromDomain);
    }

    /// <summary>Elimina un indicador de la estrategia.</summary>
    [HttpDelete("{id:guid}/indicators/{type}")]
    public async Task<IResult> RemoveIndicator(Guid id, Core.Enums.IndicatorType type, CancellationToken ct)
    {
        var result = await mediator.Send(new RemoveIndicatorCommand(id, type), ct);
        return result.ToHttpResult(StrategyDto.FromDomain);
    }

    /// <summary>Agrega una regla de trading a la estrategia.</summary>
    [HttpPost("{id:guid}/rules")]
    public async Task<IResult> AddRule(Guid id, [FromBody] AddRuleRequest request, CancellationToken ct)
    {
        var conditions = request.Conditions
            .Select(c => new AddRuleCommand.ConditionItem(c.Indicator, c.Comparator, c.Value))
            .ToList();

        var result = await mediator.Send(new AddRuleCommand(
            id, request.Name, request.RuleType, request.Operator,
            conditions, request.ActionType, request.AmountUsdt), ct);

        return result.ToHttpResult(StrategyDto.FromDomain);
    }

    /// <summary>Actualiza una regla de trading existente.</summary>
    [HttpPut("{id:guid}/rules/{ruleId:guid}")]
    public async Task<IResult> UpdateRule(Guid id, Guid ruleId, [FromBody] UpdateRuleRequest request, CancellationToken ct)
    {
        var conditions = request.Conditions
            .Select(c => new UpdateRuleCommand.ConditionItem(c.Indicator, c.Comparator, c.Value))
            .ToList();

        var result = await mediator.Send(new UpdateRuleCommand(
            id, ruleId, request.Name, request.Operator,
            conditions, request.ActionType, request.AmountUsdt), ct);

        return result.ToHttpResult(StrategyDto.FromDomain);
    }

    /// <summary>Elimina una regla de la estrategia.</summary>
    [HttpDelete("{id:guid}/rules/{ruleId:guid}")]
    public async Task<IResult> RemoveRule(Guid id, Guid ruleId, CancellationToken ct)
    {
        var result = await mediator.Send(new RemoveRuleCommand(id, ruleId), ct);
        return result.ToHttpResult(StrategyDto.FromDomain);
    }

    /// <summary>Guarda los rangos de optimización para reutilización futura.</summary>
    [HttpPut("{id:guid}/optimization-profile")]
    public async Task<IResult> SaveOptimizationProfile(
        Guid id, [FromBody] SaveOptimizationProfileRequest request, CancellationToken ct)
    {
        var ranges = request.Ranges
            .Select(r => new Core.ValueObjects.SavedParameterRange(r.Name, r.Min, r.Max, r.Step))
            .ToList();

        var result = await mediator.Send(
            new SaveOptimizationProfileCommand(id, ranges), ct);

        return result.ToHttpResult(StrategyDto.FromDomain);
    }

    /// <summary>Devuelve las plantillas de estrategias predefinidas.</summary>
    [HttpGet("templates")]
    public IResult GetTemplates()
    {
        return Results.Ok(StrategyTemplates.All);
    }
}

// ── Request DTOs ──────────────────────────────────────────────────────────

public sealed record CreateStrategyRequest(
    string                   Name,
    string                   Symbol,
    Core.Enums.TradingMode   Mode,
    decimal                  MaxOrderAmountUsdt,
    decimal                  MaxDailyLossUsdt,
    decimal                  StopLossPercent,
    decimal                  TakeProfitPercent,
    int                      MaxOpenPositions,
    string?                  Description = null,
    bool                     UseAtrSizing = false,
    decimal                  RiskPercentPerTrade = 1m,
    decimal                  AtrMultiplier = 2m,
    Core.Enums.CandleInterval Timeframe = Core.Enums.CandleInterval.OneMinute,
    Core.Enums.CandleInterval? ConfirmationTimeframe = null);

public sealed record UpdateStrategyRequest(
    string                    Name,
    string?                   Symbol,
    Core.Enums.TradingMode?   Mode,
    decimal                   MaxOrderAmountUsdt,
    decimal                   MaxDailyLossUsdt,
    decimal                   StopLossPercent,
    decimal                   TakeProfitPercent,
    int                       MaxOpenPositions,
    string?                   Description = null,
    bool                      UseAtrSizing = false,
    decimal                   RiskPercentPerTrade = 1m,
    decimal                   AtrMultiplier = 2m,
    Core.Enums.CandleInterval? Timeframe = null,
    Core.Enums.CandleInterval? ConfirmationTimeframe = null);

public sealed record AddIndicatorRequest(
    Core.Enums.IndicatorType        Type,
    Dictionary<string, decimal>     Parameters);

public sealed record AddRuleRequest(
    string                       Name,
    Core.Enums.RuleType          RuleType,
    Core.Enums.ConditionOperator Operator,
    List<AddRuleConditionRequest> Conditions,
    Core.Enums.ActionType        ActionType,
    decimal                      AmountUsdt);

public sealed record AddRuleConditionRequest(
    Core.Enums.IndicatorType Indicator,
    Core.Enums.Comparator    Comparator,
    decimal                  Value);

public sealed record UpdateRuleRequest(
    string                        Name,
    Core.Enums.ConditionOperator  Operator,
    List<AddRuleConditionRequest> Conditions,
    Core.Enums.ActionType         ActionType,
    decimal                       AmountUsdt);

public sealed record SaveOptimizationProfileRequest(
    List<Dtos.SavedParameterRangeDto> Ranges);
