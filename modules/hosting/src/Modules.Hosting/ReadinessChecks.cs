using Modules.Core.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using StackExchange.Redis;

using Modules.Core.Errors;

namespace Modules.Hosting;

/// <summary>Готова ли база принимать запросы.</summary>
public sealed class DatabaseReadiness(AppDbContext db, ILogger<DatabaseReadiness> log) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            await db.Database.ExecuteSqlRawAsync("SELECT 1", cancellationToken);
            return HealthCheckResult.Healthy();
        }
        catch (Exception error)
        {
            log.LogWarning("database_not_ready {ErrorType}", SafeError.Describe(error));
            return HealthCheckResult.Unhealthy("База недоступна");
        }
    }
}

/// <summary>Готов ли кэш.</summary>
public sealed class RedisReadiness(IConnectionMultiplexer redis, ILogger<RedisReadiness> log) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        if (!redis.IsConnected)
        {
            log.LogWarning("redis_not_ready {ErrorType}", "not_connected");
            return HealthCheckResult.Degraded("Кэш недоступен");
        }

        try
        {
            await redis.GetDatabase().PingAsync();
            return HealthCheckResult.Healthy();
        }
        catch (Exception error)
        {
            log.LogWarning("redis_not_ready {ErrorType}", SafeError.Describe(error));
            return HealthCheckResult.Degraded("Кэш недоступен");
        }
    }
}
