using Modules.Core.Errors;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Modules.Audit.Retention;

/// <summary>Фоновое удаление старых записей журнала.</summary>
[Modules.Core.Features.Feature("audit.retention")]
internal sealed class RetentionService(
    IServiceScopeFactory scopes,
    TimeProvider clock,
    AuditModuleOptions options,
    ILogger<RetentionService> log) : BackgroundService
{
    /// <summary>Как часто проверять.</summary>
    private static readonly TimeSpan Interval = TimeSpan.FromHours(24);

    /// <summary>Пауза перед первым прогоном.</summary>
    private static readonly TimeSpan StartupDelay = TimeSpan.FromMinutes(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.RunMaintenance)
        {
            return;
        }

        try
        {
            await Task.Delay(StartupDelay, clock, stoppingToken);
        }
        catch (OperationCanceledException)
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
            var retention = scope.ServiceProvider.GetRequiredService<AuditRetention>();

            await retention.RunAsync(cancellationToken);
        }
        // ⚠ Наружу выходит только отмена ОСТАНОВА (решение 0095).
        catch (Exception error) when (error is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            // ⚠ Необработанное исключение из ExecuteAsync с .NET 6 **роняет хост**.
            log.LogError("audit_retention_failed {Error}", SafeError.Describe(error));
        }
    }
}
