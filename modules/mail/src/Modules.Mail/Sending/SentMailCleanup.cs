using Modules.Mail.Domain;
using Modules.Core.Contracts.Settings;
using Modules.Core.Data;
using Modules.Core.Errors;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Modules.Mail.Sending;

/// <summary>Уборка отправленных и истёкших писем.</summary>
internal sealed class SentMailCleanup(
    IServiceScopeFactory scopes,
    TimeProvider clock,
    ILogger<SentMailCleanup> log) : BackgroundService
{
    /// <summary>Шаг таймера — проход стирания вложений.</summary>
    private static readonly TimeSpan Интервал = TimeSpan.FromHours(1);

    /// <summary>Раз во сколько сносится отправленное и истёкшее — как до 0120.</summary>
    private static readonly TimeSpan ИнтервалУдаления = TimeSpan.FromHours(24);

    private static readonly TimeSpan ОтсрочкаСтарта = TimeSpan.FromMinutes(1);

    /// <summary>Сколько держим отправленное.</summary>
    internal static readonly TimeSpan Срок = TimeSpan.FromDays(14);

    /// <summary>Сколько вложения ждут отправки, прежде чем стереться (решение 0120).</summary>
    internal static readonly TimeSpan СрокВложений = TimeSpan.FromHours(24);

    /// <summary>Причина письма, чьи вложения стёрты по сроку.</summary>
    internal const string ПричинаСрокаВложений =
        "Не отправлено за сутки: вложения стёрты, письмо не уйдёт. Почините почту и отправьте заново.";

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(ОтсрочкаСтарта, clock, stoppingToken);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        using var таймер = new PeriodicTimer(Интервал, clock);

        // ⚠ Первый проход удаляет сразу, как до 0120; дальше — раз в сутки по часам процесса.
        DateTimeOffset? удалено = null;

        do
        {
            var удалять = удалено is null || clock.GetUtcNow() - удалено.Value >= ИнтервалУдаления;

            await УбратьAsync(удалять, stoppingToken);

            if (удалять)
            {
                удалено = clock.GetUtcNow();
            }
        }
        while (await ЖдатьAsync(таймер, stoppingToken));
    }

    /// <summary>Один проход уборки.</summary>
    public static async Task<int> RunAsync(
        AppDbContext db, TimeProvider clock, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(clock);

        var порог = clock.GetUtcNow() - Срок;

        return await db.Set<MailOutbox>()
            .Where(письмо => (письмо.SentAt != null && письмо.SentAt < порог)
                || (письмо.SentAt == null && письмо.ExpiresAt != null && письмо.ExpiresAt < порог))
            .ExecuteDeleteAsync(cancellationToken);
    }

    /// <summary>Стереть вложения мёртвых писем, которые насос не стёр сам (решение 0120).</summary>
    public static Task<int> ForgetDeadAttachmentsAsync(
        AppDbContext db, int maxAttempts, TimeProvider clock, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(clock);

        var сейчас = clock.GetUtcNow();
        var сдалось = MailDelivery.Сдалось;
        var стёрто = MailAttachments.Стёрто;

        return db.Set<MailOutbox>()
            .Where(письмо => письмо.SentAt == null
                && письмо.SealedAttachments != null
                && письмо.SealedAttachments.Length > 0
                && (письмо.NextAttemptAt >= сдалось
                    || (письмо.Attempts >= maxAttempts && письмо.NextAttemptAt <= сейчас)
                    || (письмо.ExpiresAt != null && письмо.ExpiresAt <= сейчас)))
            .ExecuteUpdateAsync(
                set => set
                    .SetProperty(письмо => письмо.SealedAttachments, стёрто)
                    .SetProperty(письмо => письмо.NextAttemptAt, сдалось),
                cancellationToken);
    }

    /// <summary>Стереть вложения писем, не ушедших за <see cref="СрокВложений"/>, и похоронить их (решение 0120).</summary>
    public static Task<int> ForgetStaleAttachmentsAsync(
        AppDbContext db, TimeProvider clock, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(clock);

        var сейчас = clock.GetUtcNow();
        var порог = сейчас - СрокВложений;
        var сдалось = MailDelivery.Сдалось;
        var стёрто = MailAttachments.Стёрто;

        return db.Set<MailOutbox>()
            .Where(письмо => письмо.SentAt == null
                && письмо.SealedAttachments != null
                && письмо.SealedAttachments.Length > 0
                && письмо.CreatedAt < порог
                && письмо.NextAttemptAt <= сейчас)
            .ExecuteUpdateAsync(
                set => set
                    .SetProperty(письмо => письмо.SealedAttachments, стёрто)
                    .SetProperty(письмо => письмо.NextAttemptAt, сдалось)
                    .SetProperty(
                        письмо => письмо.LastError,
                        письмо => письмо.LastError == null
                            ? ПричинаСрокаВложений
                            : ПричинаСрокаВложений + " Последняя ошибка: " + письмо.LastError),
                cancellationToken);
    }

    private async Task УбратьAsync(bool удалять, CancellationToken cancellationToken)
    {
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

            if (удалять)
            {
                var снесено = await RunAsync(db, clock, cancellationToken);

                // ⚠ Пустой проход молчит: суточное расписание, пишущее «снесено 0», за год даёт триста шестьдесят пять строк ни о чём.
                if (снесено > 0)
                {
                    log.LogInformation("mail_cleanup {Removed}", снесено);
                }
            }

            var политика = await MailPolicy.ReadAsync(scope.ServiceProvider.GetRequiredService<ISettingsReader>(), cancellationToken);
            var стёрто = await ForgetDeadAttachmentsAsync(db, политика.MaxAttempts, clock, cancellationToken);
            var просрочено = await ForgetStaleAttachmentsAsync(db, clock, cancellationToken);

            // ⚠ Тоже молча, когда стирать нечего: у установки без вложений проход раз в час пуст всегда.
            if (стёрто > 0)
            {
                log.LogInformation("mail_attachments_forgotten {Letters}", стёрто);
            }

            if (просрочено > 0)
            {
                log.LogWarning("mail_attachments_expired {Letters}", просрочено);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ошибка)
        {
            log.LogError("mail_cleanup_failed {Error}", SafeError.Describe(ошибка));
        }
    }

    private static async Task<bool> ЖдатьAsync(PeriodicTimer таймер, CancellationToken stoppingToken)
    {
        try
        {
            return await таймер.WaitForNextTickAsync(stoppingToken);
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }
}
