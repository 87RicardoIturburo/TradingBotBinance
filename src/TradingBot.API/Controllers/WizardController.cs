using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using TradingBot.API.Middleware;
using TradingBot.Application.Wizard;

namespace TradingBot.API.Controllers;

[ApiController]
[Authorize]
[Route("api/[controller]")]
public sealed class WizardController(ISender mediator) : ControllerBase
{
    /// <summary>
    /// Ejecuta el Setup Wizard: configura Risk Budget, escanea el mercado,
    /// elige las mejores estrategias y las activa automáticamente.
    /// </summary>
    [HttpPost]
    public async Task<IResult> RunWizard(
        [FromBody] SetupWizardRequestDto request,
        CancellationToken ct)
    {
        var command = new RunSetupWizardCommand(
            request.CapitalUsdt,
            request.RiskProfile,
            request.MonitoringFrequency,
            request.TradingMode);

        var result = await mediator.Send(command, ct);
        return result.ToHttpResult();
    }
}

public sealed record SetupWizardRequestDto(
    decimal CapitalUsdt,
    string RiskProfile,
    string MonitoringFrequency,
    string TradingMode);
