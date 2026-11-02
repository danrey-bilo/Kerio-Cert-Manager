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

    /// <summary>
    /// Сколько ждать следующего прохода и опрашивать ли в нём KMP. ⚠ Решается до ожидания: сравнение часов после него
    /// на самой границе срока давало «рано», и опрос переносился на целый интервал.
    /// </summary>
    internal static (TimeSpan Ждать, bool Опросить) СледующийПроход(bool очередь, TimeSpan интервал, TimeSpan сОпроса)
    {
        var осталось = интервал - сОпроса;

        // Срок ближе паузы: опрос после неё, Kerio успевает перезапустить HTTPS.
        if (осталось <= ПаузаОчереди)
        {
            return (ПаузаОчереди, true);
        }

        return очередь ? (ПаузаОчереди, false) : (осталось, true);
    }

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

        var опросить = true;
        var опрошено = clock.GetUtcNow();

        while (!stoppingToken.IsCancellationRequested)
        {
            var интервал = ПоУмолчанию;
            var очередь = false;

            try
            {
                await using var scope = scopes.CreateAsyncScope();

                var настройки = scope.ServiceProvider.GetRequiredService<ISettingsReader>();
                интервал = TimeSpan.FromMinutes(await настройки.GetAsync(CertificateSourceSettings.IntervalMinutes, stoppingToken));

                очередь = await scope.ServiceProvider.GetRequiredService<SyncCycle>().RunAsync(full: опросить, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ошибка)
            {
                log.LogError("kerio_sync_failed {Error}", SafeError.Describe(ошибка));
            }

            // Неудачный проход с опросом тоже считается: повтор — через интервал, а не сразу.
            if (опросить)
            {
                опрошено = clock.GetUtcNow();
            }

            var (ждать, опросСледующим) = СледующийПроход(очередь, интервал, clock.GetUtcNow() - опрошено);

            try
            {
                // ⚠ Сигнал (кнопка, сохранение настроек) — всегда полный проход с опросом KMP.
                опросить = await wakeup.WaitAsync(ждать, stoppingToken) || опросСледующим;
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }
}
