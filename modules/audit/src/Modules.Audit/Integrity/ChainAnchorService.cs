using Modules.Core.Errors;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Modules.Audit.Integrity;

/// <summary>Фоновая постановка суточного якоря.</summary>
internal sealed class ChainAnchorService(
    IServiceScopeFactory scopes,
    TimeProvider clock,
    AuditModuleOptions options,
    ILogger<ChainAnchorService> log) : BackgroundService
{
    /// <summary>Раз в сутки — так записано в решении 0007.</summary>
    private static readonly TimeSpan Interval = TimeSpan.FromHours(24);

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

            await scope.ServiceProvider.GetRequiredService<ChainAnchor>().RunAsync(cancellationToken);
        }
        // ⚠ Наружу выходит только отмена ОСТАНОВА (решение 0095).
        catch (Exception error) when (error is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            // ⚠ Необработанное исключение из ExecuteAsync роняет хост с .NET 6.
            log.LogError("audit_chain_anchor_failed {Error}", SafeError.Describe(error));
        }
    }
}
