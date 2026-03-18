using TradingBot.Core.Entities;
using TradingBot.Core.Enums;
using TradingBot.Core.Interfaces.Repositories;

namespace TradingBot.Application.RiskManagement;

/// <summary>
/// Analiza la exposición neta del portafolio y valida que una nueva orden
/// no viole los límites de exposición configurados globalmente.
/// </summary>
internal sealed class PortfolioRiskManager
{
    private readonly IPositionRepository _positionRepository;

    public PortfolioRiskManager(IPositionRepository positionRepository)
    {
        _positionRepository = positionRepository;
    }

    /// <summary>
    /// Calcula la exposición neta por símbolo (Long - Short) en USDT.
    /// </summary>
    public async Task<IReadOnlyDictionary<string, SymbolExposure>> GetExposureBySymbolAsync(
        CancellationToken cancellationToken = default)
    {
        var openPositions = await _positionRepository.GetOpenPositionsAsync(cancellationToken);
        return CalculateExposureBySymbol(openPositions);
    }

    /// <summary>
    /// Calcula la exposición total del portafolio agrupada por dirección.
    /// </summary>
    public async Task<PortfolioExposure> GetPortfolioExposureAsync(
        CancellationToken cancellationToken = default)
    {
        var openPositions = await _positionRepository.GetOpenPositionsAsync(cancellationToken);
        return CalculatePortfolioExposure(openPositions);
    }

    /// <summary>
    /// Valida que una nueva orden no viole los límites de exposición del portafolio.
    /// </summary>
    public async Task<PortfolioValidationResult> ValidateExposureAsync(
        Order order,
        GlobalRiskSettings settings,
        CancellationToken cancellationToken = default)
    {
        var openPositions = await _positionRepository.GetOpenPositionsAsync(cancellationToken);
        var exposure = CalculatePortfolioExposure(openPositions);

        var orderExposureUsdt = order.NotionalValue;
        var orderSymbol = order.Symbol.Value;

        // 1. Límite de exposición Long
        if (settings.MaxPortfolioLongExposureUsdt > 0 && order.Side == OrderSide.Buy)
        {
            var newLong = exposure.TotalLongUsdt + orderExposureUsdt;
            if (newLong > settings.MaxPortfolioLongExposureUsdt)
                return PortfolioValidationResult.Blocked(
                    $"Exposición Long del portafolio ({newLong:F2} USDT) superaría el límite " +
                    $"({settings.MaxPortfolioLongExposureUsdt:F2} USDT).");
        }

        // 2. Límite de exposición Short
        // DES-B fix: En Spot, un Sell cierra un Long existente — no crea Short exposure.
        // Solo aplicar validación Short si NO hay posición Long que cerrar para este símbolo.
        // Reservado para futura implementación de Margin Trading / Futures.
        if (settings.MaxPortfolioShortExposureUsdt > 0 && order.Side == OrderSide.Sell)
        {
            var hasLongToClose = openPositions.Any(p =>
                p.Symbol == order.Symbol && p.Side == OrderSide.Buy);

            if (!hasLongToClose)
            {
                var newShort = exposure.TotalShortUsdt + orderExposureUsdt;
                if (newShort > settings.MaxPortfolioShortExposureUsdt)
                    return PortfolioValidationResult.Blocked(
                        $"Exposición Short del portafolio ({newShort:F2} USDT) superaría el límite " +
                        $"({settings.MaxPortfolioShortExposureUsdt:F2} USDT).");
            }
        }

        // 3. Concentración por símbolo
        if (settings.MaxExposurePerSymbolPercent > 0)
        {
            var symbolExposures = CalculateExposureBySymbol(openPositions);
            var currentSymbolExposure = symbolExposures.TryGetValue(orderSymbol, out var se)
                ? se.TotalUsdt : 0m;
            var newSymbolExposure = currentSymbolExposure + orderExposureUsdt;
            var totalExposure = exposure.TotalUsdt + orderExposureUsdt;

            if (totalExposure > 0)
            {
                var symbolPercent = newSymbolExposure / totalExposure * 100m;
                if (symbolPercent > settings.MaxExposurePerSymbolPercent)
                    return PortfolioValidationResult.Blocked(
                        $"Concentración en {orderSymbol} ({symbolPercent:F1}%) superaría el límite " +
                        $"({settings.MaxExposurePerSymbolPercent:F1}%).");
            }
        }

        return PortfolioValidationResult.Passed();
    }

    internal static IReadOnlyDictionary<string, SymbolExposure> CalculateExposureBySymbol(
        IReadOnlyList<Position> openPositions)
    {
        var result = new Dictionary<string, SymbolExposure>();

        foreach (var pos in openPositions)
        {
            var symbol = pos.Symbol.Value;
            var exposureUsdt = pos.CurrentPrice.Value * pos.Quantity.Value;

            if (!result.TryGetValue(symbol, out var existing))
                existing = new SymbolExposure(symbol, 0m, 0m);

            result[symbol] = pos.Side == OrderSide.Buy
                ? existing with { LongUsdt = existing.LongUsdt + exposureUsdt }
                : existing with { ShortUsdt = existing.ShortUsdt + exposureUsdt };
        }

        return result;
    }

    internal static PortfolioExposure CalculatePortfolioExposure(
        IReadOnlyList<Position> openPositions)
    {
        decimal totalLong = 0m, totalShort = 0m;

        foreach (var pos in openPositions)
        {
            var exposureUsdt = pos.CurrentPrice.Value * pos.Quantity.Value;
            if (pos.Side == OrderSide.Buy)
                totalLong += exposureUsdt;
            else
                totalShort += exposureUsdt;
        }

        return new PortfolioExposure(totalLong, totalShort);
    }
}

/// <summary>Exposición de un símbolo en el portafolio.</summary>
public sealed record SymbolExposure(
    string  Symbol,
    decimal LongUsdt,
    decimal ShortUsdt)
{
    /// <summary>Exposición total (Long + Short) en USDT.</summary>
    public decimal TotalUsdt => LongUsdt + ShortUsdt;

    /// <summary>Exposición neta (Long - Short) en USDT. Positivo = net long.</summary>
    public decimal NetUsdt => LongUsdt - ShortUsdt;
}

/// <summary>Exposición total del portafolio por dirección.</summary>
public sealed record PortfolioExposure(
    decimal TotalLongUsdt,
    decimal TotalShortUsdt)
{
    /// <summary>Exposición total combinada (Long + Short) en USDT.</summary>
    public decimal TotalUsdt => TotalLongUsdt + TotalShortUsdt;

    /// <summary>Exposición neta (Long - Short). Positivo = net long.</summary>
    public decimal NetUsdt => TotalLongUsdt - TotalShortUsdt;
}

/// <summary>Resultado de la validación de exposición del portafolio.</summary>
public sealed record PortfolioValidationResult(bool IsAllowed, string? Reason)
{
    public static PortfolioValidationResult Passed() => new(true, null);
    public static PortfolioValidationResult Blocked(string reason) => new(false, reason);
}
