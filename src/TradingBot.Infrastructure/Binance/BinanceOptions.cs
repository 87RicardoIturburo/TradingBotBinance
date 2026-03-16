namespace TradingBot.Infrastructure.Binance;

/// <summary>
/// Configuración de la integración con Binance.
/// Las API Keys se leen de variables de entorno — NUNCA se loguean.
/// </summary>
public sealed class BinanceOptions
{
    public const string SectionName = "Binance";

    /// <summary>Clave API. Variable de entorno: <c>BINANCE_API_KEY</c>.</summary>
    public string ApiKey { get; set; } = string.Empty;

    /// <summary>Clave secreta. Variable de entorno: <c>BINANCE_API_SECRET</c>.</summary>
    public string ApiSecret { get; set; } = string.Empty;

    /// <summary>
    /// Si <c>true</c>, conecta a Binance Testnet en lugar de producción.
    /// Valor por defecto: <c>true</c> para evitar operaciones accidentales con dinero real.
    /// Variable de entorno: <c>BINANCE_USE_TESTNET</c>.
    /// </summary>
    public bool UseTestnet { get; set; } = true;

    /// <summary>
    /// Si <c>true</c>, conecta a Binance Demo (<c>demo.binance.com</c>) en lugar de Testnet.
    /// Las keys de demo.binance.com requieren este flag.
    /// Variable de entorno: <c>BINANCE_USE_DEMO</c>.
    /// </summary>
    public bool UseDemo { get; set; }

    /// <summary>
    /// <c>true</c> si se resolvieron API Key y Secret válidos al arrancar.
    /// Los servicios que requieren credenciales (User Data Stream, Account) consultan este flag.
    /// </summary>
    public bool HasCredentials { get; set; }
}
