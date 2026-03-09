using MediatR;
using TradingBot.Core.Common;
using TradingBot.Core.Entities;
using TradingBot.Core.Interfaces.Services;

namespace TradingBot.Application.Commands.Strategies;

/// <summary>Activa una estrategia para que el StrategyEngine comience a procesar ticks.</summary>
public sealed record ActivateStrategyCommand(Guid Id) : IRequest<Result<TradingStrategy, DomainError>>;

internal sealed class ActivateStrategyCommandHandler(
    IStrategyConfigService configService) : IRequestHandler<ActivateStrategyCommand, Result<TradingStrategy, DomainError>>
{
    public Task<Result<TradingStrategy, DomainError>> Handle(
        ActivateStrategyCommand request,
        CancellationToken cancellationToken)
        => configService.ActivateAsync(request.Id, cancellationToken);
}

/// <summary>Desactiva una estrategia deteniendo el procesamiento de ticks.</summary>
public sealed record DeactivateStrategyCommand(Guid Id) : IRequest<Result<TradingStrategy, DomainError>>;

internal sealed class DeactivateStrategyCommandHandler(
    IStrategyConfigService configService) : IRequestHandler<DeactivateStrategyCommand, Result<TradingStrategy, DomainError>>
{
    public Task<Result<TradingStrategy, DomainError>> Handle(
        DeactivateStrategyCommand request,
        CancellationToken cancellationToken)
        => configService.DeactivateAsync(request.Id, cancellationToken);
}
