using KerioCertManager.Modules.KeyManager.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Modules.Core.Contracts.Settings;
using Modules.Core.Errors;

namespace KerioCertManager.Modules.Kerio.Sync;

/// <summary>Фоновый цикл: раз в <c>keymanager.poll_minutes</c> или по сигналу «синхронизировать сейчас».</summary>
internal sealed class SyncWorker(IServiceScopeFactory scopes, SourceWakeup wakeup, ILogger<SyncWorker> log) : BackgroundService
{
    /// <summary>Пауза после старта: дать хосту подняться и базе — ответить.</summary>
    private static readonly TimeSpan Разгон = TimeSpan.FromSeconds(15);

    private static readonly TimeSpan ПоУмолчанию = TimeSpan.FromMinutes(15);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await wakeup.WaitAsync(Разгон, stoppingToken);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            var интервал = ПоУмолчанию;

            try
            {
                await using var scope = scopes.CreateAsyncScope();

                var настройки = scope.ServiceProvider.GetRequiredService<ISettingsReader>();
                интервал = TimeSpan.FromMinutes(await настройки.GetAsync(CertificateSourceSettings.IntervalMinutes, stoppingToken));

                await scope.ServiceProvider.GetRequiredService<SyncCycle>().RunAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ошибка)
            {
                log.LogError("kerio_sync_failed {Error}", SafeError.Describe(ошибка));
            }

            try
            {
                await wakeup.WaitAsync(интервал, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }
}
