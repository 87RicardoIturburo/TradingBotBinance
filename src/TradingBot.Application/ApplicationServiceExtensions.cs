using System.Globalization;
using FluentValidation;
using MediatR;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using TradingBot.Application.Backtesting;
using TradingBot.Application.Behaviors;
using TradingBot.Application.Diagnostics;
using TradingBot.Application.RiskManagement;
using TradingBot.Application.AutoPilot;
using TradingBot.Application.Rules;
using TradingBot.Application.Scanner;
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

            // Risk Budget Guardian
            var budgetSection = configuration.GetSection(RiskBudgetConfig.SectionName);
            services.Configure<RiskBudgetConfig>(opts =>
            {
                if (decimal.TryParse(budgetSection[nameof(RiskBudgetConfig.TotalCapitalUsdt)],
                        NumberStyles.Number, CultureInfo.InvariantCulture, out var tc))
                    opts.TotalCapitalUsdt = tc;
                if (decimal.TryParse(budgetSection[nameof(RiskBudgetConfig.MaxLossPercent)],
                        NumberStyles.Number, CultureInfo.InvariantCulture, out var mlp))
                    opts.MaxLossPercent = mlp;
                if (decimal.TryParse(budgetSection[nameof(RiskBudgetConfig.ReducedThresholdPercent)],
                        NumberStyles.Number, CultureInfo.InvariantCulture, out var rtp))
                    opts.ReducedThresholdPercent = rtp;
                if (decimal.TryParse(budgetSection[nameof(RiskBudgetConfig.CriticalThresholdPercent)],
                        NumberStyles.Number, CultureInfo.InvariantCulture, out var ctp))
                    opts.CriticalThresholdPercent = ctp;
                if (decimal.TryParse(budgetSection[nameof(RiskBudgetConfig.CloseOnlyThresholdPercent)],
                        NumberStyles.Number, CultureInfo.InvariantCulture, out var cotp))
                    opts.CloseOnlyThresholdPercent = cotp;
                if (decimal.TryParse(budgetSection[nameof(RiskBudgetConfig.ReducedMultiplier)],
                        NumberStyles.Number, CultureInfo.InvariantCulture, out var rm))
                    opts.ReducedMultiplier = rm;
                if (decimal.TryParse(budgetSection[nameof(RiskBudgetConfig.CriticalMultiplier)],
                        NumberStyles.Number, CultureInfo.InvariantCulture, out var cm))
                    opts.CriticalMultiplier = cm;
                if (int.TryParse(budgetSection[nameof(RiskBudgetConfig.CriticalMaxOpenPositions)],
                        NumberStyles.Integer, CultureInfo.InvariantCulture, out var cmop))
                    opts.CriticalMaxOpenPositions = cmop;
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
            services.Configure<RiskBudgetConfig>(_ => { });
        }

        // Servicios de dominio
        services.AddScoped<IStrategyConfigService, StrategyConfigService>();
        services.AddScoped<IOrderSyncHandler, OrderSyncHandler>();
        services.AddScoped<IOrderService, OrderService>();
        services.AddScoped<PortfolioRiskManager>();
        services.AddScoped<IRiskManager, RiskManager>();
        services.AddScoped<IRuleEngine, RuleEngine>();
        services.AddScoped<IRiskBudget, RiskBudgetService>();

        // Lock de ejecución de órdenes — singleton (semáforo global por quote asset)
        services.AddSingleton<IOrderExecutionLock, OrderExecutionLock>();

        // Circuit breaker global — singleton (estado compartido entre todos los servicios)
        services.AddSingleton<IGlobalCircuitBreaker, GlobalCircuitBreaker>();

        // Estrategia por defecto (transient para que cada instancia tenga su estado)
        services.AddTransient<ITradingStrategy, DefaultTradingStrategy>();
        services.AddTransient<DefaultTradingStrategy>();
        services.AddTransient<TrendingTradingStrategy>();
        services.AddTransient<RangingTradingStrategy>();
        services.AddTransient<BearishTradingStrategy>();

        // Resolver de estrategia por régimen — singleton (cachea instancias por strategyId)
        services.AddSingleton<StrategyResolver>();

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

        // IMP-1: Reconciliación periódica Binance ↔ DB
        services.AddHostedService<BinanceReconciliationWorker>();

        // Market Scanner
        services.AddScoped<IMarketScanner, MarketScannerService>();
        if (configuration is not null)
        {
            var scannerSection = configuration.GetSection(MarketScannerConfig.SectionName);
            services.Configure<MarketScannerConfig>(opts =>
            {
                if (bool.TryParse(scannerSection[nameof(MarketScannerConfig.Enabled)], out var en))
                    opts.Enabled = en;
                if (int.TryParse(scannerSection[nameof(MarketScannerConfig.ScanIntervalMinutes)],
                        NumberStyles.Integer, CultureInfo.InvariantCulture, out var sim))
                    opts.ScanIntervalMinutes = sim;
                if (int.TryParse(scannerSection[nameof(MarketScannerConfig.TopSymbolsCount)],
                        NumberStyles.Integer, CultureInfo.InvariantCulture, out var tsc))
                    opts.TopSymbolsCount = tsc;
                var qa = scannerSection[nameof(MarketScannerConfig.QuoteAsset)];
                if (!string.IsNullOrWhiteSpace(qa))
                    opts.QuoteAsset = qa;
                if (decimal.TryParse(scannerSection[nameof(MarketScannerConfig.MinVolume24hUsdt)],
                        NumberStyles.Number, CultureInfo.InvariantCulture, out var mv))
                    opts.MinVolume24hUsdt = mv;
                if (int.TryParse(scannerSection[nameof(MarketScannerConfig.VolumeWeight)],
                        NumberStyles.Integer, CultureInfo.InvariantCulture, out var vw))
                    opts.VolumeWeight = vw;
                if (int.TryParse(scannerSection[nameof(MarketScannerConfig.SpreadWeight)],
                        NumberStyles.Integer, CultureInfo.InvariantCulture, out var sw2))
                    opts.SpreadWeight = sw2;
                if (int.TryParse(scannerSection[nameof(MarketScannerConfig.AtrWeight)],
                        NumberStyles.Integer, CultureInfo.InvariantCulture, out var aw))
                    opts.AtrWeight = aw;
                if (int.TryParse(scannerSection[nameof(MarketScannerConfig.RegimeWeight)],
                        NumberStyles.Integer, CultureInfo.InvariantCulture, out var rw))
                    opts.RegimeWeight = rw;
                if (int.TryParse(scannerSection[nameof(MarketScannerConfig.AdxWeight)],
                        NumberStyles.Integer, CultureInfo.InvariantCulture, out var adw))
                    opts.AdxWeight = adw;
                if (int.TryParse(scannerSection[nameof(MarketScannerConfig.FeeViabilityWeight)],
                        NumberStyles.Integer, CultureInfo.InvariantCulture, out var fvw))
                    opts.FeeViabilityWeight = fvw;
            });
        }
        else
        {
            services.Configure<MarketScannerConfig>(_ => { });
        }
        services.AddHostedService<MarketScannerWorker>();

        // AutoPilot / Strategy Rotator
        services.AddScoped<IStrategyRotator, StrategyRotatorService>();
        if (configuration is not null)
        {
            var autoPilotSection = configuration.GetSection(AutoPilotConfig.SectionName);
            services.Configure<AutoPilotConfig>(opts =>
            {
                if (bool.TryParse(autoPilotSection[nameof(AutoPilotConfig.Enabled)], out var en))
                    opts.Enabled = en;
                if (int.TryParse(autoPilotSection[nameof(AutoPilotConfig.RotationCooldownMinutes)],
                        NumberStyles.Integer, CultureInfo.InvariantCulture, out var rcm))
                    opts.RotationCooldownMinutes = rcm;
                if (bool.TryParse(autoPilotSection[nameof(AutoPilotConfig.ClosePositionsOnRotation)], out var cpor))
                    opts.ClosePositionsOnRotation = cpor;
                var hva = autoPilotSection[nameof(AutoPilotConfig.HighVolatilityAction)];
                if (!string.IsNullOrWhiteSpace(hva))
                    opts.HighVolatilityAction = hva;
                var tt = autoPilotSection[nameof(AutoPilotConfig.TrendingTemplateId)];
                if (!string.IsNullOrWhiteSpace(tt))
                    opts.TrendingTemplateId = tt;
                var rt = autoPilotSection[nameof(AutoPilotConfig.RangingTemplateId)];
                if (!string.IsNullOrWhiteSpace(rt))
                    opts.RangingTemplateId = rt;
                var bt = autoPilotSection[nameof(AutoPilotConfig.BearishTemplateId)];
                if (!string.IsNullOrWhiteSpace(bt))
                    opts.BearishTemplateId = bt;
            });
        }
        else
        {
            services.Configure<AutoPilotConfig>(_ => { });
        }
        services.AddHostedService<AutoPilotWorker>();

        return services;
    }
}
