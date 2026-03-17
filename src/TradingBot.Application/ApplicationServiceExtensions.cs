using System.Globalization;
using FluentValidation;
using MediatR;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using TradingBot.Application.Backtesting;
using TradingBot.Application.Behaviors;
using TradingBot.Application.Diagnostics;
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
    public static IServiceCollection AddApplication(this IServiceCollection services, IConfiguration? configuration = null)
    {
        var assembly = typeof(ApplicationServiceExtensions).Assembly;

        // MediatR + pipeline behaviors
        services.AddMediatR(cfg => cfg.RegisterServicesFromAssembly(assembly));
        services.AddTransient(typeof(IPipelineBehavior<,>), typeof(ValidationBehavior<,>));

        // FluentValidation
        services.AddValidatorsFromAssembly(assembly);

        // Configuración global de riesgo
        if (configuration is not null)
        {
            var section = configuration.GetSection(GlobalRiskSettings.SectionName);
            services.Configure<GlobalRiskSettings>(opts =>
            {
                if (decimal.TryParse(section[nameof(GlobalRiskSettings.MaxDailyLossUsdt)],
                        NumberStyles.Number, CultureInfo.InvariantCulture, out var mdl))
                    opts.MaxDailyLossUsdt = mdl;
                if (int.TryParse(section[nameof(GlobalRiskSettings.MaxGlobalOpenPositions)],
                        NumberStyles.Integer, CultureInfo.InvariantCulture, out var mop))
                    opts.MaxGlobalOpenPositions = mop;
                if (decimal.TryParse(section[nameof(GlobalRiskSettings.MaxPortfolioLongExposureUsdt)],
                        NumberStyles.Number, CultureInfo.InvariantCulture, out var mple))
                    opts.MaxPortfolioLongExposureUsdt = mple;
                if (decimal.TryParse(section[nameof(GlobalRiskSettings.MaxPortfolioShortExposureUsdt)],
                        NumberStyles.Number, CultureInfo.InvariantCulture, out var mpse))
                    opts.MaxPortfolioShortExposureUsdt = mpse;
                if (decimal.TryParse(section[nameof(GlobalRiskSettings.MaxExposurePerSymbolPercent)],
                        NumberStyles.Number, CultureInfo.InvariantCulture, out var mesp))
                    opts.MaxExposurePerSymbolPercent = mesp;
                if (decimal.TryParse(section[nameof(GlobalRiskSettings.MaxAccountDrawdownPercent)],
                        NumberStyles.Number, CultureInfo.InvariantCulture, out var madd))
                    opts.MaxAccountDrawdownPercent = madd;
            });

            // Usar InvariantCulture para evitar errores de locale en sistemas es-ES:
            // en español "." es separador de miles, por lo que "0.001" se parsearía como 1.0
            var feeSection = configuration.GetSection(TradingFeeConfig.SectionName);
            services.Configure<TradingFeeConfig>(opts =>
            {
                if (decimal.TryParse(feeSection[nameof(TradingFeeConfig.MakerFeePercent)],
                        NumberStyles.Number, CultureInfo.InvariantCulture, out var mf))
                    opts.MakerFeePercent = mf;
                if (decimal.TryParse(feeSection[nameof(TradingFeeConfig.TakerFeePercent)],
                        NumberStyles.Number, CultureInfo.InvariantCulture, out var tf))
                    opts.TakerFeePercent = tf;
                if (bool.TryParse(feeSection[nameof(TradingFeeConfig.UseBnbDiscount)], out var bnb))
                    opts.UseBnbDiscount = bnb;
                if (decimal.TryParse(feeSection[nameof(TradingFeeConfig.SlippagePercent)],
                        NumberStyles.Number, CultureInfo.InvariantCulture, out var sp))
                    opts.SlippagePercent = sp;
            });
        }
        else
        {
            services.Configure<GlobalRiskSettings>(_ => { });
            services.Configure<TradingFeeConfig>(_ => { });
        }

        // Servicios de dominio
        services.AddScoped<IStrategyConfigService, StrategyConfigService>();
        services.AddScoped<IOrderSyncHandler, OrderSyncHandler>();
        services.AddScoped<IOrderService, OrderService>();
        services.AddScoped<PortfolioRiskManager>();
        services.AddScoped<IRiskManager, RiskManager>();
        services.AddScoped<IRuleEngine, RuleEngine>();

        // Lock de ejecución de órdenes — singleton (semáforo global por quote asset)
        services.AddSingleton<IOrderExecutionLock, OrderExecutionLock>();

        // Circuit breaker global — singleton (estado compartido entre todos los servicios)
        services.AddSingleton<IGlobalCircuitBreaker, GlobalCircuitBreaker>();

        // Estrategia por defecto (transient para que cada instancia tenga su estado)
        services.AddTransient<ITradingStrategy, DefaultTradingStrategy>();

        // Backtesting — transient (sin estado entre ejecuciones)
        services.AddTransient<BacktestEngine>();

        // Métricas de trading — singleton (contadores globales)
        services.AddSingleton<TradingMetrics>();

        // StrategyEngine — singleton registrado como IHostedService + IStrategyEngine
        services.AddSingleton<StrategyEngine>();
        services.AddSingleton<IStrategyEngine>(sp => sp.GetRequiredService<StrategyEngine>());
        services.AddHostedService(sp => sp.GetRequiredService<StrategyEngine>());

        // Limit order timeout — cancela órdenes Limit que no se llenaron a tiempo
        services.AddHostedService<LimitOrderTimeoutWorker>();

        return services;
    }
}
