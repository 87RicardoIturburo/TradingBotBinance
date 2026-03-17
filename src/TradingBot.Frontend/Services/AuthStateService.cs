namespace TradingBot.Frontend.Services;

/// <summary>
/// Servicio que gestiona el estado de autenticación del frontend.
/// Usa la cookie HttpOnly emitida por el backend (BFF pattern).
/// </summary>
public sealed class AuthStateService(TradingApiClient apiClient)
{
    private bool? _isAuthenticated;

    /// <summary>Indica si el usuario está autenticado (null = desconocido).</summary>
    public bool IsAuthenticated => _isAuthenticated == true;

    /// <summary>Evento disparado al cambiar el estado de autenticación.</summary>
    public event Action? OnAuthStateChanged;

    /// <summary>Verifica el estado de autenticación consultando al backend.</summary>
    public async Task<bool> CheckAuthStatusAsync()
    {
        try
        {
            var status = await apiClient.GetAuthStatusAsync();
            _isAuthenticated = status;
        }
        catch
        {
            _isAuthenticated = false;
        }

        OnAuthStateChanged?.Invoke();
        return _isAuthenticated == true;
    }

    /// <summary>Inicia sesión enviando la API Key al backend.</summary>
    public async Task<bool> LoginAsync(string apiKey)
    {
        try
        {
            var success = await apiClient.LoginAsync(apiKey);
            _isAuthenticated = success;
        }
        catch
        {
            _isAuthenticated = false;
        }

        OnAuthStateChanged?.Invoke();
        return _isAuthenticated == true;
    }

    /// <summary>Cierra la sesión.</summary>
    public async Task LogoutAsync()
    {
        try
        {
            await apiClient.LogoutAsync();
        }
        catch
        {
            // Limpiar estado local aunque falle la llamada al backend
        }

        _isAuthenticated = false;
        OnAuthStateChanged?.Invoke();
    }
}
