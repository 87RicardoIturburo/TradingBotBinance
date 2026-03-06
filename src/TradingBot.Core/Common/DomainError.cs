namespace TradingBot.Core.Common;

/// <summary>
/// Representa un error de dominio tipado, usado con <see cref="Result{TValue,TError}"/>.
/// </summary>
public sealed record DomainError(string Code, string Message)
{
    public static DomainError Validation(string message)
        => new("VALIDATION_ERROR", message);

    public static DomainError NotFound(string resource)
        => new("NOT_FOUND", $"'{resource}' no encontrado.");

    public static DomainError Conflict(string message)
        => new("CONFLICT", message);

    public static DomainError ExternalService(string message)
        => new("EXTERNAL_SERVICE_ERROR", message);

    public static DomainError RiskLimitExceeded(string message)
        => new("RISK_LIMIT_EXCEEDED", message);

    public static DomainError InvalidOperation(string message)
        => new("INVALID_OPERATION", message);
}