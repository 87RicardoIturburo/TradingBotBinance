using Microsoft.AspNetCore.Components.WebAssembly.Http;

namespace TradingBot.Frontend.Services;

/// <summary>
/// Delegating handler que agrega <c>BrowserRequestCredentials.Include</c>
/// a todas las solicitudes HTTP, para que el navegador envíe la cookie
/// de autenticación HttpOnly en requests cross-origin (BFF pattern).
/// </summary>
internal sealed class CookieDelegatingHandler : DelegatingHandler
{
    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        request.SetBrowserRequestCredentials(BrowserRequestCredentials.Include);
        return base.SendAsync(request, cancellationToken);
    }
}
