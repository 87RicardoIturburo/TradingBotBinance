namespace TradingBot.Core.Common;

/// <summary>
/// Raíz de agregado. Define el límite transaccional del agregado
/// y es el único punto de entrada para modificar su estado interno.
/// Incluye un número de versión para soporte de concurrencia optimista (EF Core).
/// </summary>
public abstract class AggregateRoot<TId> : Entity<TId> where TId : notnull
{
    /// <summary>
    /// Versión del agregado. EF Core la usa como row-version para
    /// detectar conflictos de concurrencia optimista.
    /// </summary>
    public int Version { get; protected set; }

    protected AggregateRoot(TId id) : base(id) { }
}
