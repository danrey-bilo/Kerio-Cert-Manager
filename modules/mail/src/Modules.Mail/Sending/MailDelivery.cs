using Modules.Mail.Domain;
using Modules.Mail.Secrets;
using Modules.Mail.Templates;
using Modules.Core.Contracts.Settings;
using Modules.Crypto;
using Modules.Core.Data;
using Modules.Core.Errors;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Modules.Mail.Sending;

/// <summary>Разбор очереди: захват порции, отправка, отметка исхода.</summary>
internal readonly record struct MailRun(int Taken, int Sent);

internal sealed class MailDelivery(
    AppDbContext db,
    IMailSender sender,
    ISettingsReader settings,
    ISecretEnvelope envelope,
    TimeProvider clock,
    ILogger<MailDelivery> log,
    SystemSecrets? secrets = null,
    MailModuleOptions? options = null)
{
    /// <summary>Сколько писем берём за проход.</summary>
    internal const int Порция = 20;

    /// <summary>Контекст AEAD тела письма — тот же, что при постановке.</summary>
    private const string Контекст = "eams:mail-body";

    /// <summary>Захват порции: увеличить попытку, отодвинуть следующую и вернуть взятое.</summary>
    private const string ЗахватSql = """
        UPDATE mail_outbox
        SET attempts = attempts + 1,
            next_attempt_at = {2} + make_interval(mins => {3})
        WHERE id IN (
            SELECT id FROM mail_outbox
            WHERE sent_at IS NULL AND attempts < {0} AND next_attempt_at <= {2}
              AND (expires_at IS NULL OR expires_at > {2})
            ORDER BY next_attempt_at
            LIMIT {1}
            FOR UPDATE SKIP LOCKED
        )
        RETURNING id, to_address, subject, body, html_body, data_key, NULL::bytea AS attachments, kind, created_at,
                  next_attempt_at, attempts, sent_at, last_error, expires_at
        """;

    /// <summary>Сколько минут захваченная порция принадлежит насосу, взявшему её.</summary>
    internal const int АрендаМинут = 30;

    /// <summary>Срок «следующей попытки» у письма, исчерпавшего попытки, — оно больше не берётся.</summary>
    internal static readonly DateTimeOffset Сдалось = new(9999, 1, 1, 0, 0, 0, TimeSpan.Zero);

    /// <summary>Разобрать очередную порцию.</summary>
    public async Task<MailRun> RunAsync(CancellationToken cancellationToken = default)
    {
        var политика = await MailPolicy.ReadAsync(settings, cancellationToken);

        if (!политика.Configured)
        {
            // ⚠ Молча: насос ходит раз в пятнадцать секунд, и строка «почта не настроена» залила бы лог собой у всякой установки, которая почтой не.
            return default;
        }

        var порция = await db.Set<MailOutbox>()
            .FromSqlRaw(ЗахватSql, политика.MaxAttempts, Порция, clock.GetUtcNow(), АрендаМинут)
            .AsNoTracking()
            .ToListAsync(cancellationToken);

        if (порция.Count == 0)
        {
            // ⚠ Пустой прогон МОЛЧИТ.
            return default;
        }

        var ушло = 0;

        var аренда = порция.ToDictionary(письмо => письмо.Id, письмо => письмо.NextAttemptAt);

        // ⚠ Голый пароль, заданный до привязки, привязывается ДО писем порции (решение 0120), одним разом на порцию, а не на каждое письмо.
        await ПривязатьПарольAsync(политика, CancellationToken.None);

        // ⚠ ⚠ ⚠ ОДНО СОЕДИНЕНИЕ НА ПОРЦИЮ (этап 60.2).
        await using var сессия = sender.Open(политика);

        // ⚠ ⚠ Соединение с базой открыто на всю порцию (решение 0120): замок письма — сессионный и живёт на соединении.
        await db.Database.OpenConnectionAsync(cancellationToken);

        try
        {
            for (var номер = 0; номер < порция.Count; номер++)
            {
                var письмо = порция[номер];
                var попытка = new Попытка();
                var заперто = false;

                // ⚠ Каждое письмо в СВОЕЙ попытке: одно упавшее не уносит пачку.
                try
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    // ⚠ ⚠ Замок письма — до продления аренды и до отметки исхода включительно: отмена ждёт его и после видит либо «ушло», либо «не наше».
                    await ЗаперетьAsync(письмо.Id, cancellationToken);
                    заперто = true;

                    switch (await ОтправитьAsync(сессия, письмо, аренда, попытка, cancellationToken))
                    {
                        case Исход.Ушло:
                            ушло++;
                            break;

                        case Исход.Чужое:
                            await ПропуститьЧужоеAsync(письмо, CancellationToken.None);
                            break;

                        case Исход.НеУшло:
                            await ОтказатьAsync(письмо, попытка.Ошибка!, политика.MaxAttempts, CancellationToken.None);
                            break;

                        default:
                            break;
                    }
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    // ⚠ Останов процесса — не неудача письма.
                    await ВернутьAsync([.. порция.Skip(попытка.Начато ? номер + 1 : номер).Select(п => (п.Id, аренда[п.Id]))]);

                    throw;
                }
                catch (Exception ошибка)
                {
                    await ОтказатьAsync(письмо, ошибка, политика.MaxAttempts, CancellationToken.None);
                }
                finally
                {
                    if (заперто)
                    {
                        await ОтпуститьAsync(письмо.Id, CancellationToken.None);
                    }
                }
            }
        }
        finally
        {
            await db.Database.CloseConnectionAsync();
        }

        log.LogInformation("mail_delivered {Sent} {Taken}", ушло, порция.Count);

        return new MailRun(порция.Count, ушло);
    }

    /// <summary>Чем кончилась отправка одного письма под замком его номера.</summary>
    private enum Исход
    {
        /// <summary>Ушло и отмечено.</summary>
        Ушло,

        /// <summary>Письмо больше не наше: взял другой насос или отменили — пропущено, не отправлялось.</summary>
        Чужое,

        /// <summary>Отправка отказала; ошибка — в <see cref="Попытка.Ошибка"/>.</summary>
        НеУшло,

        /// <summary>Ушло, а отметка не легла — строка лога уже написана.</summary>
        НеОтмечено,

        /// <summary>Вложения письма стёрты: не отправлялось, похоронено.</summary>
        Стёрто,
    }

    /// <summary>Как далеко зашла отправка одного письма: её видят и цикл, и повтор стратегии выполнения.</summary>
    private sealed class Попытка
    {
        /// <summary>Отправка начата — реле могло письмо принять; останов такое письмо в очередь не возвращает.</summary>
        public bool Начато { get; set; }

        /// <summary>Отказ отправки.</summary>
        public Exception? Ошибка { get; set; }
    }

    /// <summary>Что вернуло продление аренды: новый срок и вложения письма, прочитанные под замком письма.</summary>
    private sealed class Продление
    {
        public DateTimeOffset Срок { get; set; }

        public byte[]? Вложения { get; set; }
    }

    /// <summary>Отправить письмо под замком его номера: отменённое не уходит, отмена во время отправки ждёт.</summary>
    private async Task<Исход> ОтправитьAsync(
        IMailSession сессия,
        MailOutbox письмо,
        Dictionary<Guid, DateTimeOffset> аренда,
        Попытка попытка,
        CancellationToken cancellationToken)
    {
        // ⚠ Аренда письма — продлить и сверить перед отправкой (ревизия 2026-10-26, решение 0118).
        if (await ПродлитьAsync(письмо.Id, аренда[письмо.Id], cancellationToken) is not { } продлена)
        {
            return Исход.Чужое;
        }

        аренда[письмо.Id] = продлена.Срок;

        // ⚠ ⚠ Стёртые вложения — письмо не уходит никогда (решение 0120): поднятое в очередь руками, оно ушло бы без файлов под видом доставленного.
        if (MailAttachments.Стёрты(продлена.Вложения))
        {
            await ПохоронитьСтёртоеAsync(письмо.Id, CancellationToken.None);

            log.LogWarning("mail_attachments_erased_not_sent {Kind}", письмо.Kind);

            return Исход.Стёрто;
        }

        var ключ = new WrappedDataKey(письмо.DataKey);
        var тело = envelope.OpenText(ключ, new SealedSecret(письмо.SealedBody), Контекст);
        var вложения = MailAttachments.Open(envelope, ключ, продлена.Вложения);

        // ⚠ HTML-часть (решение 0121) — тем же ключом данных, своим контекстом; нет её — письмо одним текстом.
        var html = письмо.SealedHtml is { } запечатано
            ? envelope.OpenText(ключ, new SealedSecret(запечатано), MailHtml.Контекст)
            : null;

        попытка.Начато = true;

        try
        {
            await сессия.SendAsync(new OutgoingMail(письмо.ToAddress, письмо.Subject, тело, вложения, html), cancellationToken);
        }
        catch (Exception ошибка) when (ошибка is not OperationCanceledException)
        {
            попытка.Ошибка = ошибка;

            return Исход.НеУшло;
        }

        try
        {
            await ОтметитьУспехAsync(письмо.Id, CancellationToken.None);
        }
        catch (Exception ошибка)
        {
            log.LogError("mail_sent_mark_failed {Kind} {Error}", письмо.Kind, SafeError.Describe(ошибка));

            return Исход.НеОтмечено;
        }

        return Исход.Ушло;
    }

    /// <summary>Класс замка письма: «MLET».</summary>
    internal const int ЗамокПисьма = 0x4D4C4554;

    /// <summary>Взять замок письма на соединении порции; ждёт, пока его держит отмена.</summary>
    private async Task ЗаперетьAsync(Guid id, CancellationToken cancellationToken) =>
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT pg_advisory_lock({ЗамокПисьма}, hashtext({id}::text))", cancellationToken);

    /// <summary>Отпустить замок письма.</summary>
    private async Task ОтпуститьAsync(Guid id, CancellationToken cancellationToken)
    {
        try
        {
            await db.Database.ExecuteSqlInterpolatedAsync(
                $"SELECT pg_advisory_unlock({ЗамокПисьма}, hashtext({id}::text))", cancellationToken);
        }
        catch (Exception ошибка)
        {
            log.LogError("mail_unlock_failed {Error}", SafeError.Describe(ошибка));
        }
    }

    /// <summary>Продлить аренду письма, если оно всё ещё наше; <c>null</c> — его взял другой насос или отменили.</summary>
    private async Task<Продление?> ПродлитьAsync(Guid id, DateTimeOffset прежняя, CancellationToken cancellationToken)
    {
        var сроки = await db.Database
            .SqlQuery<Продление>($"UPDATE mail_outbox SET next_attempt_at = {clock.GetUtcNow()} + make_interval(mins => {АрендаМинут}) WHERE id = {id} AND sent_at IS NULL AND next_attempt_at = {прежняя} RETURNING next_attempt_at AS \"Срок\", attachments AS \"Вложения\"")
            .ToListAsync(cancellationToken);

        return сроки.Count == 0 ? null : сроки[0];
    }

    /// <summary>Письмо не наше: отменено — сказать об этом, иначе аренду забрал другой насос.</summary>
    private async Task ПропуститьЧужоеAsync(MailOutbox письмо, CancellationToken cancellationToken)
    {
        var сдалось = Сдалось;

        var отменено = await db.Set<MailOutbox>().AnyAsync(
            п => п.Id == письмо.Id
                && п.SentAt == null
                && п.NextAttemptAt >= сдалось
                && п.LastError != null
                && п.LastError.StartsWith(MailQueue.ПриставкаОтмены),
            cancellationToken);

        if (отменено)
        {
            log.LogInformation("mail_cancelled_before_send {Kind}", письмо.Kind);
        }
        else
        {
            log.LogWarning("mail_lease_lost {Kind}", письмо.Kind);
        }
    }

    /// <summary>Отказ письма: описание в лог и в строку, пауза повтора или «сдалось».</summary>
    private async Task ОтказатьAsync(MailOutbox письмо, Exception ошибка, int попыток, CancellationToken cancellationToken)
    {
        var описание = ошибка is MailConfigurationException ? ошибка.Message : SafeError.Describe(ошибка);

        log.LogWarning(
            "mail_attempt_failed {Kind} {Attempt} {Error}",
            письмо.Kind,
            письмо.Attempts,
            описание);

        await ОтметитьНеудачуAsync(письмо.Id, письмо.Attempts, попыток, описание, cancellationToken);
    }

    /// <summary>Привязать голый пароль SMTP к настройкам до первой отправки порции (решение 0120).</summary>
    private async Task ПривязатьПарольAsync(MailPolicy политика, CancellationToken cancellationToken)
    {
        if (secrets is null
            || options is not { BindSmtpPassword: true, Features.StoredPassword: true }
            || string.IsNullOrWhiteSpace(политика.Username))
        {
            return;
        }

        try
        {
            await secrets.BindLegacyAsync(SystemSecretNames.SmtpPassword, SmtpSender.Цель(политика), cancellationToken);
        }
        catch (Exception ошибка) when (ошибка is not OperationCanceledException)
        {
        }
    }

    /// <summary>Вернуть неначатые письма в очередь: попытка не потрачена, взять можно сразу.</summary>
    private async Task ВернутьAsync(IReadOnlyList<(Guid Id, DateTimeOffset Аренда)> остаток)
    {
        if (остаток.Count == 0)
        {
            return;
        }

        var ids = остаток.Select(письмо => письмо.Id).ToArray();
        var сроки = остаток.Select(письмо => письмо.Аренда).ToArray();

        try
        {
            await db.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE mail_outbox SET attempts = attempts - 1, next_attempt_at = {clock.GetUtcNow()} FROM unnest({ids}, {сроки}) AS r(id, lease) WHERE mail_outbox.id = r.id AND mail_outbox.next_attempt_at = r.lease AND mail_outbox.sent_at IS NULL",
                CancellationToken.None);
        }
        catch (Exception ошибка)
        {
            log.LogError("mail_return_failed {Error}", SafeError.Describe(ошибка));
        }
    }

    /// <summary>Отметить успех и стереть вложения: письмо ушло, хранить их больше незачем (решение 0120).</summary>
    private async Task ОтметитьУспехAsync(Guid id, CancellationToken cancellationToken)
    {
        var стёрто = MailAttachments.Стёрто;

        await db.Set<MailOutbox>()
            .Where(письмо => письмо.Id == id)
            .ExecuteUpdateAsync(
                set => set
                    .SetProperty(письмо => письмо.SentAt, clock.GetUtcNow())
                    .SetProperty(письмо => письмо.LastError, (string?)null)
                    .SetProperty(письмо => письмо.SealedAttachments, письмо => письмо.SealedAttachments == null ? null : стёрто),
                cancellationToken);
    }

    /// <summary>Похоронить письмо со стёртыми вложениями, поднятое в очередь: без файлов оно не уйдёт.</summary>
    private async Task ПохоронитьСтёртоеAsync(Guid id, CancellationToken cancellationToken) =>
        await db.Set<MailOutbox>()
            .Where(письмо => письмо.Id == id)
            .ExecuteUpdateAsync(
                set => set
                    .SetProperty(письмо => письмо.NextAttemptAt, Сдалось)
                    .SetProperty(письмо => письмо.LastError, ПричинаСтёрто),
                cancellationToken);

    /// <summary>Причина письма, которое нашлось в очереди со стёртыми вложениями.</summary>
    internal const string ПричинаСтёрто = "Вложения письма стёрты: без файлов оно не уйдёт. Отправьте его заново.";

    /// <summary>Отметить неудачу.</summary>
    private async Task ОтметитьНеудачуAsync(Guid id, int попытка, int попыток, string описание, CancellationToken cancellationToken)
    {
        var последняя = попытка >= попыток;
        var следующая = последняя ? Сдалось : clock.GetUtcNow().AddMinutes(попытка * попытка);
        var сдалось = Сдалось;
        var стёрто = MailAttachments.Стёрто;

        try
        {
            await db.Set<MailOutbox>()
                .Where(письмо => письмо.Id == id && письмо.NextAttemptAt < сдалось)
                .ExecuteUpdateAsync(
                    set => set
                        .SetProperty(письмо => письмо.LastError, описание)
                        .SetProperty(письмо => письмо.NextAttemptAt, следующая)
                        .SetProperty(
                            письмо => письмо.SealedAttachments,
                            письмо => последняя && письмо.SealedAttachments != null ? стёрто : письмо.SealedAttachments),
                    cancellationToken);
        }
        catch (Exception ошибка)
        {
            log.LogError("mail_failure_mark_failed {Error}", SafeError.Describe(ошибка));
        }
    }
}
