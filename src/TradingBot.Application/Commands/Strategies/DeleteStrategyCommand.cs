using MediatR;
using TradingBot.Core.Common;
using TradingBot.Core.Interfaces.Services;

namespace TradingBot.Application.Commands.Strategies;

/// <summary>Elimina una estrategia. No permite eliminar una que esté activa.</summary>
public sealed record DeleteStrategyCommand(Guid Id) : IRequest<Result<bool, DomainError>>;

internal sealed class DeleteStrategyCommandHandler(
    IStrategyConfigService configService) : IRequestHandler<DeleteStrategyCommand, Result<bool, DomainError>>
{
    public Task<Result<bool, DomainError>> Handle(
        DeleteStrategyCommand request,
        CancellationToken cancellationToken)
        => configService.DeleteAsync(request.Id, cancellationToken);
}
