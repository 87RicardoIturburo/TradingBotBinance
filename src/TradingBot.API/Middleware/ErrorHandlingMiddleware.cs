using System.Text.Json;
using FluentValidation;
using TradingBot.Core.Common;

namespace TradingBot.API.Middleware;

/// <summary>
/// Middleware global de error handling. Mapea excepciones y <see cref="DomainError"/>
/// a códigos HTTP apropiados con respuesta JSON consistente.
/// </summary>
internal sealed class ErrorHandlingMiddleware(
    RequestDelegate next,
    ILogger<ErrorHandlingMiddleware> logger)
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public async Task InvokeAsync(HttpContext context)
    {
        try
        {
            await next(context);
        }
        catch (ValidationException ex)
        {
            logger.LogWarning("Validation error: {Errors}", ex.Errors);
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            context.Response.ContentType = "application/problem+json";

            var errors = ex.Errors
                .GroupBy(e => e.PropertyName)
                .ToDictionary(g => g.Key, g => g.Select(e => e.ErrorMessage).ToArray());

            await context.Response.WriteAsJsonAsync(new
            {
                type = "https://tools.ietf.org/html/rfc9110#section-15.5.1",
                title = "Validation Error",
                status = 400,
                errors
            }, JsonOptions);
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
            // Request cancelado por el cliente — no loguear como error
            context.Response.StatusCode = StatusCodes.Status499ClientClosedRequest;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error no controlado en {Method} {Path}", context.Request.Method, context.Request.Path);
            context.Response.StatusCode = StatusCodes.Status500InternalServerError;
            context.Response.ContentType = "application/problem+json";

            await context.Response.WriteAsJsonAsync(new
            {
                type = "https://tools.ietf.org/html/rfc9110#section-15.6.1",
                title = "Internal Server Error",
                status = 500,
                detail = context.RequestServices.GetRequiredService<IHostEnvironment>().IsDevelopment()
                    ? ex.Message
                    : "Ocurrió un error interno. Contacta al administrador."
            }, JsonOptions);
        }
    }
}

/// <summary>
/// Métodos de extensión para mapear <see cref="Result{TValue,TError}"/> a <see cref="IResult"/>.
/// </summary>
internal static class ResultExtensions
{
    /// <summary>
    /// Convierte un <see cref="Result{TValue,TError}"/> de dominio en una respuesta HTTP.
    /// </summary>
    public static IResult ToHttpResult<TValue>(
        this Result<TValue, DomainError> result,
        Func<TValue, object>? map = null,
        int successStatusCode = StatusCodes.Status200OK)
        where TValue : notnull
    {
        if (result.IsSuccess)
        {
            var body = map is not null ? map(result.Value) : result.Value;
            return successStatusCode == StatusCodes.Status201Created
                ? Results.Created(string.Empty, body)
                : Results.Ok(body);
        }

        return result.Error.Code switch
        {
            "NOT_FOUND"          => Results.NotFound(new { error = result.Error.Message }),
            "VALIDATION_ERROR"   => Results.BadRequest(new { error = result.Error.Message }),
            "CONFLICT"           => Results.Conflict(new { error = result.Error.Message }),
            "INVALID_OPERATION"  => Results.UnprocessableEntity(new { error = result.Error.Message }),
            "RISK_LIMIT_EXCEEDED"=> Results.UnprocessableEntity(new { error = result.Error.Message }),
            _                    => Results.Problem(result.Error.Message)
        };
    }
}
