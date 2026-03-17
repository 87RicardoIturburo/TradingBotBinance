namespace TradingBot.Core.Enums;

public enum TradingMode
{
    Live,
    Testnet,
    PaperTrading,
    /// <summary>
    /// Conecta a WebSocket real y procesa señales, pero no ejecuta ni simula órdenes.
    /// Solo loguea lo que habría hecho. Útil para validar estrategias sin riesgo.
    /// </summary>
    DryRun
}