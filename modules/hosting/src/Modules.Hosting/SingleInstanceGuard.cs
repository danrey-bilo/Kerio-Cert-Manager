using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Modules.Hosting;

/// <summary>Кто держит инвариант «экземпляр каждой роли ровно один».</summary>
public interface IInstanceGuard : IHostedService, IHealthCheck;

/// <summary>Берёт замок единственного экземпляра при старте и держит его всё время работы.</summary>
public sealed class SingleInstanceGuard(SingleInstanceOptions options, ILogger<SingleInstance> log)
    : IInstanceGuard
{
    private SingleInstance? _замок;

    /// <summary>Взять замок.</summary>
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (_замок is not null)
        {
            return;
        }

        // ⚠ Строку и роль даёт хост (решение 0107): откуда берутся секреты подключения и как зовётся переменная роли — знание установки, а не модуля.
        _замок = await SingleInstance.AcquireAsync(
            options.ConnectionString(), options.Role, log, cancellationToken, options.LockClass, options.Help);
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_замок is not null)
        {
            await _замок.DisposeAsync();
            _замок = null;
        }
    }

    /// <summary>Готовность: замок всё ещё наш.</summary>
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        if (_замок is null)
        {
            return HealthCheckResult.Unhealthy("Замок единственного экземпляра не взят");
        }

        return await _замок.HeldAsync(cancellationToken)
            ? HealthCheckResult.Healthy()
            : HealthCheckResult.Unhealthy(
                "Замок единственного экземпляра потерян и не возвращён: "
                + "возможно, место занял другой процесс");
    }
}

/// <summary>Что замку нужно от хоста: строка подключения к PostgreSQL и роль процесса.</summary>
public sealed record SingleInstanceOptions(
    Func<string> ConnectionString, string Role, int LockClass = SingleInstance.DefaultLockClass, string? Help = null);
