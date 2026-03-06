namespace TradingBot.Core.Common;

/// <summary>
/// Tipo discriminado para representar éxito o fallo sin usar excepciones.
/// </summary>
public readonly record struct Result<TValue, TError>
{
    private readonly TValue? _value;
    private readonly TError? _error;

    public bool IsSuccess { get; }
    public bool IsFailure => !IsSuccess;

    public TValue Value => IsSuccess
        ? _value!
        : throw new InvalidOperationException("No se puede acceder a Value en un resultado fallido.");

    public TError Error => IsFailure
        ? _error!
        : throw new InvalidOperationException("No se puede acceder a Error en un resultado exitoso.");

    private Result(TValue value)
    {
        _value   = value;
        _error   = default;
        IsSuccess = true;
    }

    private Result(TError error)
    {
        _error   = error;
        _value   = default;
        IsSuccess = false;
    }

    public static Result<TValue, TError> Success(TValue value)  => new(value);
    public static Result<TValue, TError> Failure(TError error)  => new(error);

    /// <summary>Proyecta el resultado a un nuevo valor.</summary>
    public TResult Match<TResult>(
        Func<TValue, TResult> onSuccess,
        Func<TError, TResult> onFailure)
        => IsSuccess ? onSuccess(_value!) : onFailure(_error!);

    /// <summary>Ejecuta un efecto según el resultado.</summary>
    public void Match(Action<TValue> onSuccess, Action<TError> onFailure)
    {
        if (IsSuccess) onSuccess(_value!);
        else           onFailure(_error!);
    }
}