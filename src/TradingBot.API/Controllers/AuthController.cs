using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using System.Security.Claims;
using TradingBot.API.Authentication;

namespace TradingBot.API.Controllers;

/// <summary>
/// Controlador de autenticación BFF (Backend For Frontend).
/// El frontend envía la API Key una sola vez en POST /login;
/// el backend responde con una cookie HttpOnly que autentica las demás solicitudes.
/// De esta forma la API Key nunca se almacena en archivos estáticos del frontend.
/// </summary>
[ApiController]
[Route("api/[controller]")]
public sealed class AuthController(
    IOptionsMonitor<ApiKeyAuthenticationOptions> apiKeyOptions) : ControllerBase
{
    /// <summary>Autentica al usuario con la API Key y emite una cookie HttpOnly.</summary>
    [HttpPost("login")]
    [AllowAnonymous]
    public async Task<IResult> Login([FromBody] LoginRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        var expectedKey = apiKeyOptions.CurrentValue.ApiKey;

        // Si no hay key configurada (modo desarrollo abierto), permitir login sin key
        if (string.IsNullOrWhiteSpace(expectedKey))
        {
            await SignInUserAsync();
            return Results.Ok(new { authenticated = true });
        }

        if (string.IsNullOrWhiteSpace(request.ApiKey))
            return Results.Problem("API Key requerida.", statusCode: 401);

        if (!string.Equals(request.ApiKey, expectedKey, StringComparison.Ordinal))
            return Results.Problem("API Key inválida.", statusCode: 401);

        await SignInUserAsync();
        return Results.Ok(new { authenticated = true });
    }

    /// <summary>Cierra la sesión eliminando la cookie de autenticación.</summary>
    [HttpPost("logout")]
    [AllowAnonymous]
    public async Task<IResult> Logout()
    {
        await HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        return Results.Ok(new { authenticated = false });
    }

    /// <summary>Verifica si el usuario tiene una sesión activa.</summary>
    [HttpGet("status")]
    [AllowAnonymous]
    public IResult GetStatus()
    {
        var isAuthenticated = User.Identity?.IsAuthenticated == true;
        return Results.Ok(new { authenticated = isAuthenticated });
    }

    private async Task SignInUserAsync()
    {
        var claims = new[]
        {
            new Claim(ClaimTypes.Name, "TradingBotUser"),
            new Claim(ClaimTypes.Role, "Admin")
        };

        var identity = new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme);
        var principal = new ClaimsPrincipal(identity);

        await HttpContext.SignInAsync(
            CookieAuthenticationDefaults.AuthenticationScheme,
            principal,
            new AuthenticationProperties
            {
                IsPersistent = true,
                ExpiresUtc = DateTimeOffset.UtcNow.AddHours(24)
            });
    }
}

/// <summary>Solicitud de login con API Key.</summary>
public sealed record LoginRequest(string? ApiKey);
