using FluentValidation;
using TradingBot.Application.Commands.Strategies;

namespace TradingBot.Application.Validators;

internal sealed class CreateStrategyCommandValidator : AbstractValidator<CreateStrategyCommand>
{
    public CreateStrategyCommandValidator()
    {
        RuleFor(x => x.Name)
            .NotEmpty().WithMessage("El nombre de la estrategia es requerido.")
            .MaximumLength(100).WithMessage("El nombre no puede superar 100 caracteres.");

        RuleFor(x => x.SymbolValue)
            .NotEmpty().WithMessage("El símbolo es requerido.")
            .MaximumLength(20).WithMessage("El símbolo no puede superar 20 caracteres.")
            .Matches(@"^[A-Z0-9]+$").WithMessage("El símbolo solo puede contener letras mayúsculas y números.");

        RuleFor(x => x.MaxOrderAmountUsdt)
            .GreaterThan(0).WithMessage("El monto máximo por orden debe ser mayor que cero.");

        RuleFor(x => x.MaxDailyLossUsdt)
            .GreaterThan(0).WithMessage("La pérdida máxima diaria debe ser mayor que cero.");

        RuleFor(x => x.StopLossPercent)
            .InclusiveBetween(0, 100).WithMessage("El stop-loss debe estar entre 0 y 100%.");

        RuleFor(x => x.TakeProfitPercent)
            .InclusiveBetween(0, 100).WithMessage("El take-profit debe estar entre 0 y 100%.");

        RuleFor(x => x.MaxOpenPositions)
            .GreaterThan(0).WithMessage("El número máximo de posiciones debe ser mayor que cero.");

        RuleFor(x => x.Mode)
            .IsInEnum().WithMessage("El modo de trading no es válido.");
    }
}
