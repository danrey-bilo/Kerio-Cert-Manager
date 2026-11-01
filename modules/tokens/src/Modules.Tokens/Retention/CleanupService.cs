using Modules.Core.Errors;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Modules.Tokens.Retention;

/// <summary>Фоновая уборка мёртвых токенов и их секретов.</summary>
internal sealed class CleanupService(
    IServiceScopeFactory scopes,
    TimeProvider clock,
    ILogger<CleanupService> log) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromHours(24);

    private static readonly TimeSpan StartupDelay = TimeSpan.FromMinutes(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(StartupDelay, clock, stoppingToken);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        using var timer = new PeriodicTimer(Interval, clock);

        do
        {
            await RunOnceAsync(stoppingToken);
        }
        while (await SafeWaitAsync(timer, stoppingToken));
    }

    private async Task RunOnceAsync(CancellationToken stoppingToken)
    {
        try
        {
            await using var scope = scopes.CreateAsyncScope();

            var cleanup = scope.ServiceProvider.GetRequiredService<TokenCleanup>();
            var result = await cleanup.RunAsync(stoppingToken);

            if (result.Any)
            {
                log.LogInformation("token_cleanup {Tokens} {Secrets}", result.Tokens, result.Secrets);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception error)
        {
            log.LogError("session_cleanup_failed {Error}", SafeError.Describe(error));
        }
    }

    private static async Task<bool> SafeWaitAsync(PeriodicTimer timer, CancellationToken stoppingToken)
    {
        try
        {
            return await timer.WaitForNextTickAsync(stoppingToken);
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }
}
