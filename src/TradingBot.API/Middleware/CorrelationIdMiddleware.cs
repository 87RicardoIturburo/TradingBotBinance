using Serilog.Context;

namespace TradingBot.API.Middleware;

/// <summary>
/// Middleware que genera o propaga un Correlation ID por cada request HTTP.
/// <list type="bullet">
///   <item>Si el request trae header <c>X-Correlation-Id</c>, lo reutiliza.</item>
///   <item>Si no, genera uno nuevo (GUID corto).</item>
///   <item>Lo inyecta en el <see cref="LogContext"/> de Serilog para que todos los logs
///         del request lo incluyan automáticamente.</item>
///   <item>Lo devuelve en el response header para rastreo end-to-end.</item>
/// </list>
/// </summary>
internal sealed class CorrelationIdMiddleware(RequestDelegate next)
{
    private const string HeaderName = "X-Correlation-Id";

    public async Task InvokeAsync(HttpContext context)
    {
        var correlationId = context.Request.Headers[HeaderName].FirstOrDefault()
                            ?? Guid.NewGuid().ToString("N")[..12];

        context.Response.OnStarting(() =>
        {
            context.Response.Headers[HeaderName] = correlationId;
            return Task.CompletedTask;
        });

        using (LogContext.PushProperty("CorrelationId", correlationId))
        {
            await next(context);
        }
    }
}
