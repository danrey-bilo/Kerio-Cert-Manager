using KerioCertManager.Modules.KeyManager.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Modules.Core.Contracts.Settings;
using Modules.Core.Errors;

namespace KerioCertManager.Modules.Kerio.Sync;

/// <summary>
/// Фоновый цикл: раз в <c>keymanager.poll_minutes</c> или по сигналу «синхронизировать сейчас». Пока очередь установки
/// не пуста, следующий проход — после паузы Kerio, без нового опроса Key Manager Plus.
/// </summary>
internal sealed class SyncWorker(IServiceScopeFactory scopes, SourceWakeup wakeup, TimeProvider clock, ILogger<SyncWorker> log)
    : BackgroundService
{
    /// <summary>Пауза после старта: дать хосту подняться и базе — ответить.</summary>
    private static readonly TimeSpan Разгон = TimeSpan.FromSeconds(15);

    private static readonly TimeSpan ПоУмолчанию = TimeSpan.FromMinutes(15);

    /// <summary>Между проходами очереди: Kerio успевает перезапустить HTTPS после прошлой установки.</summary>
    private static TimeSpan ПаузаОчереди => SyncCycle.ПаузаПослеИзменения + TimeSpan.FromSeconds(5);

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

        DateTimeOffset? опрошено = null;

        while (!stoppingToken.IsCancellationRequested)
        {
            var интервал = ПоУмолчанию;
            var очередь = false;

            try
            {
                await using var scope = scopes.CreateAsyncScope();

                var настройки = scope.ServiceProvider.GetRequiredService<ISettingsReader>();
                интервал = TimeSpan.FromMinutes(await настройки.GetAsync(CertificateSourceSettings.IntervalMinutes, stoppingToken));

                var опросить = опрошено is not { } когда || clock.GetUtcNow() - когда >= интервал;

                очередь = await scope.ServiceProvider.GetRequiredService<SyncCycle>().RunAsync(full: опросить, stoppingToken);

                if (опросить)
                {
                    опрошено = clock.GetUtcNow();
                }
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
                // ⚠ Сигнал (кнопка, сохранение настроек) — всегда полный проход с опросом KMP.
                if (await wakeup.WaitAsync(очередь && ПаузаОчереди < интервал ? ПаузаОчереди : интервал, stoppingToken))
                {
                    опрошено = null;
                }
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }
}
