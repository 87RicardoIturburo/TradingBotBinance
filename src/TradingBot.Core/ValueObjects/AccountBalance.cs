namespace TradingBot.Core.ValueObjects;

/// <summary>
/// Balance de un asset en la cuenta Binance.
/// </summary>
public sealed record AccountBalance(
    string  Asset,
    decimal Free,
    decimal Locked)
{
    public decimal Total => Free + Locked;
}
