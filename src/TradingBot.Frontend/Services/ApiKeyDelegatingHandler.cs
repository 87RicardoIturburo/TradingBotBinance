namespace TradingBot.Frontend.Services;

/// <summary>
/// Delegating handler que agrega el header <c>X-Api-Key</c> a todas las
/// solicitudes HTTP salientes hacia la API del backend.
/// La key se lee de la configuración del frontend (appsettings.json).
/// </summary>
internal sealed class ApiKeyDelegatingHandler(string apiKey) : DelegatingHandler
{
    private const string HeaderName = "X-Api-Key";

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(apiKey))
            request.Headers.TryAddWithoutValidation(HeaderName, apiKey);

        return base.SendAsync(request, cancellationToken);
    }
}
