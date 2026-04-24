using TradingBot.Core.Entities;
using TradingBot.Core.Enums;

namespace TradingBot.Core.Interfaces.Services;

/// <summary>
/// Fábrica de estrategias Pool con template por defecto cuando no hay BaseTemplateId en DB.
/// Produce siempre la misma configuración determinística de indicadores y reglas.
/// </summary>
public interface IDefaultPoolTemplateFactory
{
    TradingStrategy CreateForSymbol(string symbol, TradingMode mode, CandleInterval timeframe);
}
