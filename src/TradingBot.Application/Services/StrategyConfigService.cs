using Microsoft.Extensions.Logging;
using TradingBot.Core.Common;
using TradingBot.Core.Entities;
using TradingBot.Core.Interfaces;
using TradingBot.Core.Interfaces.Repositories;
using TradingBot.Core.Interfaces.Services;

namespace TradingBot.Application.Services;

/// <summary>
/// Servicio de configuración de estrategias con hot-reload.
/// Gestiona el ciclo de vida completo CRUD + activación/desactivación.
/// Invalida el caché de Redis al modificar y notifica al StrategyEngine.
/// </summary>
internal sealed class StrategyConfigService : IStrategyConfigService
{
    private readonly IStrategyRepository              _repository;
    private readonly IUnitOfWork                      _unitOfWork;
    private readonly ICacheService                    _cache;
    private readonly IStrategyEngine                  _strategyEngine;
    private readonly ILogger<StrategyConfigService>   _logger;

    private const string CachePrefix     = "strategy:";
    private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(10);

    public StrategyConfigService(
        IStrategyRepository            repository,
        IUnitOfWork                    unitOfWork,
        ICacheService                  cache,
        IStrategyEngine                strategyEngine,
        ILogger<StrategyConfigService> logger)
    {
        _repository     = repository;
        _unitOfWork     = unitOfWork;
        _cache          = cache;
        _strategyEngine = strategyEngine;
        _logger         = logger;
    }

    public async Task<Result<TradingStrategy, DomainError>> GetByIdAsync(
        Guid id,
        CancellationToken cancellationToken = default)
    {
        var cached = await _cache.GetAsync<TradingStrategy>(
            $"{CachePrefix}{id}", cancellationToken);

        if (cached is not null)
            return Result<TradingStrategy, DomainError>.Success(cached);

        var strategy = await _repository.GetWithRulesAsync(id, cancellationToken);
        if (strategy is null)
            return Result<TradingStrategy, DomainError>.Failure(
                DomainError.NotFound($"Estrategia '{id}'"));

        await _cache.SetAsync($"{CachePrefix}{id}", strategy, CacheTtl, cancellationToken);
        return Result<TradingStrategy, DomainError>.Success(strategy);
    }

    public async Task<IReadOnlyList<TradingStrategy>> GetAllAsync(
        CancellationToken cancellationToken = default)
        => await _repository.GetAllAsync(cancellationToken);

    public async Task<IReadOnlyList<TradingStrategy>> GetAllActiveAsync(
        CancellationToken cancellationToken = default)
        => await _repository.GetActiveStrategiesAsync(cancellationToken);

    public async Task<Result<TradingStrategy, DomainError>> CreateAsync(
        TradingStrategy strategy,
        CancellationToken cancellationToken = default)
    {
        await _repository.AddAsync(strategy, cancellationToken);
        await _unitOfWork.SaveChangesAsync(cancellationToken);

        await _cache.SetAsync($"{CachePrefix}{strategy.Id}", strategy, CacheTtl, cancellationToken);
        _logger.LogInformation("Estrategia '{Name}' ({Id}) creada", strategy.Name, strategy.Id);

        return Result<TradingStrategy, DomainError>.Success(strategy);
    }

    public async Task<Result<TradingStrategy, DomainError>> UpdateAsync(
        TradingStrategy strategy,
        CancellationToken cancellationToken = default)
    {
        await _repository.UpdateAsync(strategy, cancellationToken);
        await _unitOfWork.SaveChangesAsync(cancellationToken);

        await _cache.SetAsync($"{CachePrefix}{strategy.Id}", strategy, CacheTtl, cancellationToken);
        _logger.LogInformation("Estrategia '{Name}' ({Id}) actualizada", strategy.Name, strategy.Id);

        await _strategyEngine.ReloadStrategyAsync(strategy.Id, cancellationToken);

        return Result<TradingStrategy, DomainError>.Success(strategy);
    }

    public async Task<Result<TradingStrategy, DomainError>> ActivateAsync(
        Guid id,
        CancellationToken cancellationToken = default)
    {
        var strategy = await _repository.GetWithRulesAsync(id, cancellationToken);
        if (strategy is null)
            return Result<TradingStrategy, DomainError>.Failure(
                DomainError.NotFound($"Estrategia '{id}'"));

        var result = strategy.Activate();
        if (result.IsFailure)
            return result;

        await _repository.UpdateAsync(strategy, cancellationToken);
        await _unitOfWork.SaveChangesAsync(cancellationToken);
        await _cache.SetAsync($"{CachePrefix}{id}", strategy, CacheTtl, cancellationToken);

        _logger.LogInformation("Estrategia '{Name}' ({Id}) activada", strategy.Name, id);

        await _strategyEngine.ReloadStrategyAsync(id, cancellationToken);

        return result;
    }

    public async Task<Result<TradingStrategy, DomainError>> DeactivateAsync(
        Guid id,
        CancellationToken cancellationToken = default)
    {
        var strategy = await _repository.GetByIdAsync(id, cancellationToken);
        if (strategy is null)
            return Result<TradingStrategy, DomainError>.Failure(
                DomainError.NotFound($"Estrategia '{id}'"));

        strategy.Deactivate();

        await _repository.UpdateAsync(strategy, cancellationToken);
        await _unitOfWork.SaveChangesAsync(cancellationToken);
        await _cache.RemoveAsync($"{CachePrefix}{id}", cancellationToken);

        _logger.LogInformation("Estrategia '{Name}' ({Id}) desactivada", strategy.Name, id);

        await _strategyEngine.ReloadStrategyAsync(id, cancellationToken);

        return Result<TradingStrategy, DomainError>.Success(strategy);
    }

    public async Task<Result<bool, DomainError>> DeleteAsync(
        Guid id,
        CancellationToken cancellationToken = default)
    {
        var strategy = await _repository.GetByIdAsync(id, cancellationToken);
        if (strategy is null)
            return Result<bool, DomainError>.Failure(
                DomainError.NotFound($"Estrategia '{id}'"));

        if (strategy.IsActive)
            return Result<bool, DomainError>.Failure(
                DomainError.InvalidOperation(
                    "No se puede eliminar una estrategia activa. Desactívala primero."));

        await _repository.DeleteAsync(id, cancellationToken);
        await _unitOfWork.SaveChangesAsync(cancellationToken);
        await _cache.RemoveAsync($"{CachePrefix}{id}", cancellationToken);

        _logger.LogInformation("Estrategia ({Id}) eliminada", id);
        return Result<bool, DomainError>.Success(true);
    }
}
