using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace TradingBot.API.Authentication;

/// <summary>
/// Authentication handler basado en API Key.
/// Valida el header <c>X-Api-Key</c> contra la clave configurada.
/// Si no hay key configurada, permite acceso anónimo (desarrollo sin auth).
/// </summary>
internal sealed class ApiKeyAuthenticationHandler(
    IOptionsMonitor<ApiKeyAuthenticationOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder)
    : AuthenticationHandler<ApiKeyAuthenticationOptions>(options, logger, encoder)
{
    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        // Si no hay key configurada, permitir acceso anónimo (desarrollo)
        if (string.IsNullOrWhiteSpace(Options.ApiKey))
            return Task.FromResult(AuthenticateResult.Success(CreateAnonymousTicket()));

        if (!Request.Headers.TryGetValue(ApiKeyAuthenticationOptions.HeaderName, out var apiKeyHeader))
            return Task.FromResult(AuthenticateResult.Fail("Header X-Api-Key no proporcionado."));

        var providedKey = apiKeyHeader.ToString();
        if (string.IsNullOrWhiteSpace(providedKey))
            return Task.FromResult(AuthenticateResult.Fail("API Key vacía."));

        if (!string.Equals(providedKey, Options.ApiKey, StringComparison.Ordinal))
            return Task.FromResult(AuthenticateResult.Fail("API Key inválida."));

        var claims = new[]
        {
            new Claim(ClaimTypes.Name, "TradingBotUser"),
            new Claim(ClaimTypes.Role, "Admin")
        };

        var identity  = new ClaimsIdentity(claims, Scheme.Name);
        var principal = new ClaimsPrincipal(identity);
        var ticket    = new AuthenticationTicket(principal, Scheme.Name);

        return Task.FromResult(AuthenticateResult.Success(ticket));
    }

    private AuthenticationTicket CreateAnonymousTicket()
    {
        var claims = new[] { new Claim(ClaimTypes.Name, "Anonymous") };
        var identity  = new ClaimsIdentity(claims, Scheme.Name);
        var principal = new ClaimsPrincipal(identity);
        return new AuthenticationTicket(principal, Scheme.Name);
    }
}

/// <summary>
/// Opciones de autenticación por API Key.
/// </summary>
public sealed class ApiKeyAuthenticationOptions : AuthenticationSchemeOptions
{
    public const string SchemeName = "ApiKey";
    public const string HeaderName = "X-Api-Key";
    public const string SectionName = "Authentication";

    /// <summary>API Key esperada. Se lee de variables de entorno o configuración.</summary>
    public string ApiKey { get; set; } = string.Empty;
}

/// <summary>
/// Configura la API Key de autenticación desde variables de entorno e IConfiguration.
/// Se ejecuta post-build para que las overrides de tests se apliquen correctamente.
/// </summary>
internal sealed class ConfigureApiKeyOptions(IConfiguration configuration)
    : IPostConfigureOptions<ApiKeyAuthenticationOptions>
{
    public void PostConfigure(string? name, ApiKeyAuthenticationOptions options)
    {
        // Variable de entorno tiene prioridad sobre configuración
        var key = Environment.GetEnvironmentVariable("TRADINGBOT_API_KEY")
                  ?? configuration.GetValue<string>("Authentication:ApiKey")
                  ?? string.Empty;

        if (!string.IsNullOrWhiteSpace(key))
            options.ApiKey = key;
    }
}
