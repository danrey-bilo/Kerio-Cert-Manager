using Modules.Mail.Domain;
using Modules.Mail.Templates;
using Modules.Core.Contracts.Mail;
using Modules.Core.Contracts.Settings;
using Modules.Crypto;
using Modules.Core.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using MimeKit;

namespace Modules.Mail.Sending;

/// <summary>Постановка письма в очередь.</summary>
internal sealed class MailQueue(
    AppDbContext db,
    MailTemplates templates,
    ISettingsReader settings,
    ISecretEnvelope envelope,
    TimeProvider clock,
    MailWakeup wakeup,
    ILogger<MailQueue> log) : IMailQueue
{
    /// <summary>Контекст AEAD тела письма.</summary>
    private const string Контекст = "eams:mail-body";

    public async Task<bool> CanSendAsync(CancellationToken cancellationToken = default) =>
        (await MailPolicy.ReadAsync(settings, cancellationToken)).Configured;

    public async Task<MailQueueResult> EnqueueAsync(
        string toAddress,
        string kind,
        string locale,
        IReadOnlyDictionary<string, string> values,
        DateTimeOffset? expiresAt = null,
        CancellationToken cancellationToken = default) =>
        (await EnqueueAsync(toAddress, kind, locale, values, [], expiresAt, cancellationToken)).Result;

    /// <summary>Поставить письмо с вложениями и узнать его номер (решение 0120).</summary>
    public Task<MailTicket> EnqueueAsync(
        string toAddress,
        string kind,
        string locale,
        IReadOnlyDictionary<string, string> values,
        IReadOnlyList<MailAttachment> attachments,
        DateTimeOffset? expiresAt = null,
        CancellationToken cancellationToken = default) =>
        EnqueueAsync(toAddress, kind, locale, values, MailTemplates.БезТаблиц, attachments, expiresAt, cancellationToken);

    /// <summary>Поставить письмо с таблицами и вложениями и узнать его номер (решение 0121).</summary>
    public async Task<MailTicket> EnqueueAsync(
        string toAddress,
        string kind,
        string locale,
        IReadOnlyDictionary<string, string> values,
        IReadOnlyDictionary<string, MailTable> tables,
        IReadOnlyList<MailAttachment> attachments,
        DateTimeOffset? expiresAt = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(toAddress);
        ArgumentNullException.ThrowIfNull(values);
        ArgumentNullException.ThrowIfNull(tables);
        ArgumentNullException.ThrowIfNull(attachments);

        // ⚠ ⚠ Один адрес — одно написание (решение 0110, разбор 2026-10-21, находка 35).
        var адрес = Ящик(toAddress);

        var политика = await MailPolicy.ReadAsync(settings, cancellationToken);

        if (!политика.Configured)
        {
            log.LogInformation("mail_not_configured {Kind}", kind);
            return new MailTicket(MailQueueResult.NotConfigured, null);
        }

        // ⚠ ⚠ Транзакция вызывающего обязательна (ревизия 2026-10-26, решение 0118).
        if (db.Database.CurrentTransaction is null)
        {
            throw new InvalidOperationException(
                "Постановка письма требует транзакцию вызывающего: без неё предел частоты не держится.");
        }

        // ⚠ ⚠ Виды вне предела (решение 0120, `IMailTemplateProvider.UnthrottledKinds`) не считают предел и не берут его замок.
        if (templates.IsThrottled(kind) && await ЗаПределомAsync(адрес, kind, политика.RatePerHour, cancellationToken))
        {
            log.LogWarning("mail_rate_limited {Kind} {Limit}", kind, политика.RatePerHour);
            return new MailTicket(MailQueueResult.RateLimited, null);
        }

        var письмо = templates.Отрисовать(kind, locale, values, tables);
        var ключ = envelope.CreateDataKey();
        var номер = Guid.CreateVersion7();

        db.Set<MailOutbox>().Add(new MailOutbox
        {
            Id = номер,
            ToAddress = адрес,
            Subject = письмо.Subject,
            SealedBody = envelope.SealText(ключ, письмо.Body, Контекст).Bytes,
            DataKey = ключ.Bytes,
            // ⚠ HTML-часть — тем же ключом данных, своим контекстом (решение 0121); без параметра `Html` её нет.
            SealedHtml = письмо.Html is { } html ? envelope.SealText(ключ, html, MailHtml.Контекст).Bytes : null,
            // ⚠ Тем же ключом данных, своим контекстом: ротация перекладывает одну обёртку на всё письмо.
            SealedAttachments = MailAttachments.Seal(envelope, ключ, attachments),
            Kind = kind,
            CreatedAt = clock.GetUtcNow(),
            NextAttemptAt = clock.GetUtcNow(),
            ExpiresAt = expiresAt,
        });

        // ⚠ ⚠ ⚠ БУДИМ НАСОС ДО `SaveChanges`, И ЭТО ОСОЗНАННЫЙ РАЗМЕН (этап 60.2).
        wakeup.Signal();

        return new MailTicket(MailQueueResult.Queued, номер);
    }

    /// <summary>Что стало с письмами (решение 0120).</summary>
    public async Task<IReadOnlyDictionary<Guid, MailLetterStatus>> StatusAsync(
        IReadOnlyCollection<Guid> ids, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(ids);

        if (ids.Count == 0)
        {
            return new Dictionary<Guid, MailLetterStatus>();
        }

        var номера = ids.Distinct().ToArray();
        var предел = (await MailPolicy.ReadAsync(settings, cancellationToken)).MaxAttempts;
        var сейчас = clock.GetUtcNow();

        var письма = await db.Set<MailOutbox>()
            .AsNoTracking()
            .Where(письмо => номера.Contains(письмо.Id))
            .Select(письмо => new { письмо.Id, письмо.ToAddress, письмо.SentAt, письмо.Attempts, письмо.NextAttemptAt, письмо.ExpiresAt, письмо.LastError })
            .ToListAsync(cancellationToken);

        return письма.ToDictionary(
            письмо => письмо.Id,
            письмо => new MailLetterStatus(
                письмо.SentAt is not null ? MailLetterState.Sent
                    : (письмо.Attempts >= предел && письмо.NextAttemptAt <= сейчас)
                        || письмо.NextAttemptAt >= MailDelivery.Сдалось
                        || (письмо.ExpiresAt is { } срок && срок <= сейчас) ? MailLetterState.Dead
                    : MailLetterState.Pending,
                письмо.ToAddress,
                письмо.LastError));
    }

    /// <summary>Отменить письма, которые ещё не ушли (решение 0120).</summary>
    public async Task<int> CancelAsync(
        IReadOnlyCollection<Guid> ids, string reason, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(ids);
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);

        if (ids.Count == 0)
        {
            return 0;
        }

        var номера = ids.Distinct().ToArray();
        var причина = ПриставкаОтмены + reason;

        if (db.Database.CurrentTransaction is not null)
        {
            return await ОтменитьAsync(номера, причина, cancellationToken);
        }

        // ⚠ Своя транзакция, если вызывающий её не открыл: замок письма транзакционный и обязан держаться до фиксации обновления.
        return await db.Database.CreateExecutionStrategy().ExecuteAsync(
            cancellationToken,
            async token =>
            {
                await using var транзакция = await db.Database.BeginTransactionAsync(token);

                var отменено = await ОтменитьAsync(номера, причина, token);

                await транзакция.CommitAsync(token);

                return отменено;
            });
    }

    /// <summary>Замки писем, затем условное обновление — в текущей транзакции.</summary>
    private async Task<int> ОтменитьAsync(Guid[] номера, string причина, CancellationToken cancellationToken)
    {
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT pg_advisory_xact_lock({MailDelivery.ЗамокПисьма}, к) FROM (SELECT DISTINCT hashtext(id::text) AS к FROM unnest({номера}) AS id ORDER BY к) AS ключи",
            cancellationToken);

        var сдалось = MailDelivery.Сдалось;
        var стёрто = MailAttachments.Стёрто;

        return await db.Set<MailOutbox>()
            .Where(письмо => номера.Contains(письмо.Id)
                && письмо.SentAt == null
                && письмо.NextAttemptAt < сдалось)
            .ExecuteUpdateAsync(
                set => set
                    .SetProperty(письмо => письмо.SealedAttachments, письмо => письмо.SealedAttachments == null ? null : стёрто)
                    .SetProperty(письмо => письмо.NextAttemptAt, сдалось)
                    .SetProperty(письмо => письмо.LastError, причина),
                cancellationToken);
    }

    /// <summary>Начало причины отменённого письма: по ней насос отличает отмену от потери аренды.</summary>
    internal const string ПриставкаОтмены = "Отменено: ";

    /// <summary>Исчерпан ли предел частоты на эту пару «адрес + вид».</summary>
    private async Task<bool> ЗаПределомAsync(
        string toAddress, string kind, int ratePerHour, CancellationToken cancellationToken)
    {
        // ⚠ ⚠ Счёт и вставка — под замком пары «адрес + вид» (решение 0110, находка 35).
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT pg_advisory_xact_lock({ЗамокПредела}, hashtext({toAddress + "|" + kind}))", cancellationToken);

        var от = clock.GetUtcNow().AddHours(-1);

        var сколько = await db.Set<MailOutbox>()
            .CountAsync(
                письмо => письмо.ToAddress == toAddress
                    && письмо.Kind == kind
                    && письмо.CreatedAt >= от,
                cancellationToken);

        return сколько >= ratePerHour;
    }

    /// <summary>Голый ящик получателя в нижнем регистре.</summary>
    internal static string Ящик(string toAddress)
    {
        var строка = toAddress.Trim();

        if (!MailboxAddress.TryParse(строка, out var ящик)
            || !string.IsNullOrEmpty(ящик.Name)
            || !string.Equals(ящик.Address, строка, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("Адрес получателя — один ящик без имени и комментария.", nameof(toAddress));
        }

        return ящик.Address.ToLowerInvariant();
    }

    /// <summary>Класс замка предела частоты: «MAIL».</summary>
    private const int ЗамокПредела = 0x4D41494C;
}
