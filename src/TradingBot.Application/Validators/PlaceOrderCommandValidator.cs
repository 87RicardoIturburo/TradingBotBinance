using FluentValidation;
using TradingBot.Application.Commands.Orders;

namespace TradingBot.Application.Validators;

internal sealed class PlaceOrderCommandValidator : AbstractValidator<PlaceOrderCommand>
{
    public PlaceOrderCommandValidator()
    {
        RuleFor(x => x.StrategyId)
            .NotEmpty().WithMessage("El ID de la estrategia es requerido.");

        RuleFor(x => x.SymbolValue)
            .NotEmpty().WithMessage("El símbolo es requerido.")
            .Matches(@"^[A-Z0-9]+$").WithMessage("El símbolo solo puede contener letras mayúsculas y números.");

        RuleFor(x => x.QuantityValue)
            .GreaterThan(0).WithMessage("La cantidad debe ser mayor que cero.");

        RuleFor(x => x.Side)
            .IsInEnum().WithMessage("El lado de la orden no es válido.");

        RuleFor(x => x.Type)
            .IsInEnum().WithMessage("El tipo de orden no es válido.");

        RuleFor(x => x.Mode)
            .IsInEnum().WithMessage("El modo de trading no es válido.");

        RuleFor(x => x.LimitPriceValue)
            .GreaterThan(0).When(x => x.LimitPriceValue.HasValue)
            .WithMessage("El precio límite debe ser mayor que cero.");

        RuleFor(x => x.StopPriceValue)
            .GreaterThan(0).When(x => x.StopPriceValue.HasValue)
            .WithMessage("El precio stop debe ser mayor que cero.");
    }
}
