using MediatR;
using TradingBot.Core.Common;
using TradingBot.Core.Interfaces.Services;

namespace TradingBot.Application.Scanner;

public sealed record GetTopSymbolsQuery(int TopCount = 50) : IRequest<Result<IReadOnlyList<SymbolScore>, DomainError>>;

internal sealed class GetTopSymbolsQueryHandler(IMarketScanner scanner)
    : IRequestHandler<GetTopSymbolsQuery, Result<IReadOnlyList<SymbolScore>, DomainError>>
{
    public Task<Result<IReadOnlyList<SymbolScore>, DomainError>> Handle(
        GetTopSymbolsQuery request,
        CancellationToken cancellationToken)
        => scanner.ScanAsync(request.TopCount, cancellationToken);
}
