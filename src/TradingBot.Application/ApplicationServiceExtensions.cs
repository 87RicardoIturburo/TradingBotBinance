using FluentValidation;
using MediatR;
using Microsoft.Extensions.DependencyInjection;
using TradingBot.Application.Behaviors;
using TradingBot.Application.RiskManagement;
using TradingBot.Application.Rules;
using TradingBot.Application.Services;
using TradingBot.Application.Strategies;
using TradingBot.Core.Interfaces.Services;
using TradingBot.Core.Interfaces.Trading;

namespace TradingBot.Application;

public static class ApplicationServiceExtensions
{
    /// <summary>
    /// Registra los servicios de la capa Application: MediatR, FluentValidation,
    /// motores de trading, reglas y gestión de riesgo.
    /// </summary>
    public static IServiceCollection AddApplication(this IServiceCollection services)
    {
        var assembly = typeof(ApplicationServiceExtensions).Assembly;

        // MediatR + pipeline behaviors
        services.AddMediatR(cfg => cfg.RegisterServicesFromAssembly(assembly));
        services.AddTransient(typeof(IPipelineBehavior<,>), typeof(ValidationBehavior<,>));

        // FluentValidation
        services.AddValidatorsFromAssembly(assembly);

        // Servicios de dominio
        services.AddScoped<IStrategyConfigService, StrategyConfigService>();
        services.AddScoped<IOrderService, OrderService>();
        services.AddScoped<IRiskManager, RiskManager>();
        services.AddScoped<IRuleEngine, RuleEngine>();

        // Estrategia por defecto (transient para que cada instancia tenga su estado)
        services.AddTransient<ITradingStrategy, DefaultTradingStrategy>();

        // StrategyEngine — singleton registrado como IHostedService + IStrategyEngine
        services.AddSingleton<StrategyEngine>();
        services.AddSingleton<IStrategyEngine>(sp => sp.GetRequiredService<StrategyEngine>());
        services.AddHostedService(sp => sp.GetRequiredService<StrategyEngine>());

        return services;
    }
}
