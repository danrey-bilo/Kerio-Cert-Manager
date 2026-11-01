using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;

using Modules.Core.Errors;

namespace Modules.Audit.Partitioning;

/// <summary>Третий слой защиты партиций: беда должна стать видимой до того, как станет отказом.</summary>
internal sealed class PartitionsHealthCheck(
    PartitionMaintenance partitions,
    ILogger<PartitionsHealthCheck> log) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            var missing = await partitions.MissingAsync(cancellationToken: cancellationToken);
            var defaultUsed = await partitions.DefaultPartitionHasRowsAsync(cancellationToken);

            if (missing.Count == 0 && !defaultUsed)
            {
                return HealthCheckResult.Healthy();
            }

            log.LogError(
                "audit_partitions_not_ready {Missing} {DefaultUsed}",
                string.Join(", ", missing), defaultUsed);

            return HealthCheckResult.Unhealthy(defaultUsed
                ? "Записи осели в запасной партиции — нужен ручной разбор"
                : $"Не хватает партиций: {string.Join(", ", missing)}");
        }
        catch (Exception error)
        {
            log.LogWarning("audit_partitions_check_failed {ErrorType}", SafeError.Describe(error));
            return HealthCheckResult.Unhealthy("Проверка партиций не выполнена");
        }
    }
}
