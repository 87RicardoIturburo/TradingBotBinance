namespace TradingBot.Application.Scanner;

/// <summary>
/// Configuración del Market Scanner. Hot-reloadable vía appsettings.json.
/// </summary>
public sealed class MarketScannerConfig
{
    public const string SectionName = "MarketScanner";

    public bool Enabled { get; set; } = true;
    public int ScanIntervalMinutes { get; set; } = 5;
    public int TopSymbolsCount { get; set; } = 50;
    public string QuoteAsset { get; set; } = "USDT";
    public decimal MinVolume24hUsdt { get; set; } = 1_000_000m;

    public int VolumeWeight { get; set; } = 25;
    public int SpreadWeight { get; set; } = 15;
    public int AtrWeight { get; set; } = 15;
    public int RegimeWeight { get; set; } = 15;
    public int AdxWeight { get; set; } = 10;
    public int FeeViabilityWeight { get; set; } = 20;
}
