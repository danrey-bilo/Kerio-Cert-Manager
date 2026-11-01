using Modules.Core.Errors;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Modules.Audit.Partitioning;

/// <summary>Фоновое создание партиций наперёд.</summary>
internal sealed class PartitionMaintenanceService(
    IServiceScopeFactory scopes,
    TimeProvider clock,
    AuditModuleOptions options,
    ILogger<PartitionMaintenanceService> log) : BackgroundService
{
    /// <summary>Как часто проверять.</summary>
    private static readonly TimeSpan Interval = TimeSpan.FromHours(6);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.RunMaintenance)
        {
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            await RunSafelyAsync(stoppingToken);

            try
            {
                await Task.Delay(Interval, clock, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private async Task RunSafelyAsync(CancellationToken cancellationToken)
    {
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            var partitions = scope.ServiceProvider.GetRequiredService<PartitionMaintenance>();

            var created = await partitions.EnsureAsync(cancellationToken: cancellationToken);

            if (created.Count > 0)
            {
                log.LogInformation("audit_partitions_created {Partitions}", string.Join(", ", created));
            }
        }
        // ⚠ Наружу выходит только отмена ОСТАНОВА (решение 0095).
        catch (Exception error) when (error is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            // ⚠ Необработанное исключение из ExecuteAsync с .NET 6 **роняет хост**.
            log.LogError("audit_partition_maintenance_failed {Error}", SafeError.Describe(error));
        }
    }
}
