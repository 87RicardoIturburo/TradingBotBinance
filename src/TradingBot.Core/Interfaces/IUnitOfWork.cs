namespace TradingBot.Core.Interfaces;

/// <summary>
/// Unidad de trabajo. Permite confirmar todos los cambios del ciclo de vida
/// de una operación en una única transacción.
/// Implementado por <c>TradingBotDbContext</c> en Infrastructure.
/// Los handlers MediatR lo inyectan y llaman a <see cref="SaveChangesAsync"/>
/// al final de cada comando.
/// </summary>
public interface IUnitOfWork
{
    Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);
}
