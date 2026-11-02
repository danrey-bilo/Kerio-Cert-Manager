using System.Globalization;
using System.Net.Mail;
using KerioCertManager.Modules.Kerio.Domain;
using KerioCertManager.Modules.Kerio.Localization;
using KerioCertManager.Modules.Kerio.Settings;
using KerioCertManager.Modules.Kerio.Sync;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Modules.Core.Contracts.Accounts;
using Modules.Core.Contracts.Mail;
using Modules.Core.Contracts.Settings;
using Modules.Core.Data;
using Modules.Core.Errors;

namespace KerioCertManager.Modules.Kerio.Notifications;

/// <summary>Письма администраторам о серверах Kerio: отчёты об установке и предупреждения без повторов.</summary>
internal sealed class KerioNotifier(
    AppDbContext db,
    IMailQueue mail,
    ISettingsReader settings,
    IAccounts accounts,
    ServerTexts texts,
    TimeProvider clock,
    ILogger<KerioNotifier> log)
{
    /// <summary>Строка таблицы подробностей: ключ подписи (<see cref="KerioMail.Label"/>) и значение.</summary>
    internal readonly record struct Row(string Key, string Value);

    /// <summary>Сколько хранится журнал оповещений.</summary>
    internal static readonly TimeSpan СрокЖурнала = TimeSpan.FromDays(180);

    /// <summary>
    /// Получатели писем о сервере: общие адреса, а не заданы — все учётные записи; адреса сервера идут дополнительно и
    /// общих не заменяют. <c>null</c> — только общие (проверка оповещений).
    /// </summary>
    public async Task<IReadOnlyList<string>> RecipientsAsync(KerioServer? server, CancellationToken cancellationToken = default)
    {
        IReadOnlyList<string> общие = Адреса(await settings.GetAsync(KerioSettings.AlertEmails, cancellationToken));

        if (общие.Count == 0)
        {
            общие = await accounts.Query()
                .Where(учётка => учётка.IsActive)
                .Select(учётка => учётка.Email)
                .ToListAsync(cancellationToken);
        }

        return [.. общие.Concat(server?.NotifyEmails ?? []).Distinct(StringComparer.OrdinalIgnoreCase)];
    }

    /// <summary>Адреса из строки через запятую, точку с запятой или пробел; негодные отброшены.</summary>
    internal static IReadOnlyList<string> Адреса(string? text) =>
        [.. (text ?? string.Empty)
            .Split([',', ';', ' ', '\n', '\r', '\t'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(Годен)
            .Distinct(StringComparer.OrdinalIgnoreCase)];

    /// <summary>Голый адрес почты без имени и комментария.</summary>
    internal static bool Годен(string адрес) =>
        адрес.Length <= 254
        && MailAddress.TryCreate(адрес, out var разобран)
        && string.Equals(разобран.Address, адрес, StringComparison.OrdinalIgnoreCase)
        && string.IsNullOrEmpty(разобран.DisplayName);

    /// <summary>Уровень словом — для журнала оповещений.</summary>
    internal static string Уровень(AlertLevel level) => level switch
    {
        AlertLevel.Critical => "critical",
        AlertLevel.Warning => "warning",
        _ => "info",
    };

    /// <summary>Включено ли письмо о предупреждении этого вида (вкладка «Оповещения»).</summary>
    internal async Task<bool> ВключеноAsync(string kind, CancellationToken cancellationToken) => kind switch
    {
        AlertKinds.Expiring => await settings.GetAsync(KerioSettings.AlertExpiring, cancellationToken),
        AlertKinds.Expired => await settings.GetAsync(KerioSettings.AlertExpired, cancellationToken),
        AlertKinds.SourceMissing => await settings.GetAsync(KerioSettings.AlertSourceMissing, cancellationToken),
        AlertKinds.Drift => await settings.GetAsync(KerioSettings.AlertDrift, cancellationToken),
        AlertKinds.RenewalPending => await settings.GetAsync(KerioSettings.AlertPending, cancellationToken),
        AlertKinds.Unreachable => await settings.GetAsync(KerioSettings.AlertUnreachable, cancellationToken),
        AlertKinds.AuthRejected or AlertKinds.NotAdmin or AlertKinds.NotTrusted
            => await settings.GetAsync(KerioSettings.AlertAccess, cancellationToken),
        AlertKinds.Overdue => await settings.GetAsync(KerioSettings.AlertOverdue, cancellationToken),
        AlertKinds.NameStale => await settings.GetAsync(KerioSettings.AlertDeployFailed, cancellationToken),
        _ => true,
    };

    /// <summary>Дни напоминаний из строки «7, 3, 1»: по убыванию, без повторов и негодных.</summary>
    internal static IReadOnlyList<int> Напоминания(string? text) =>
        [.. (text ?? string.Empty)
            .Split([',', ';', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(ч => int.TryParse(ч, NumberStyles.None, CultureInfo.InvariantCulture, out var д) ? д : 0)
            .Where(д => д is > 0 and <= 365)
            .Distinct()
            .OrderDescending()];

    /// <summary>Ступень напоминания: наименьший порог, в который уже вошёл срок; <c>null</c> — ещё период предупреждения.</summary>
    internal static int? Ступень(DateTimeOffset? notAfter, IReadOnlyList<int> reminders, DateTimeOffset now)
    {
        if (notAfter is not { } до)
        {
            return null;
        }

        var дней = (int)Math.Ceiling((до - now).TotalDays);
        var порог = reminders.Where(р => дней <= р).DefaultIfEmpty(0).Min();

        return порог > 0 ? порог : null;
    }

    /// <summary>Отправить письмо всем получателям сервера; <c>false</c> — почта не настроена или не вышло.</summary>
    public Task<bool> SendAsync(
        KerioServer? server,
        string kind,
        string title,
        string message,
        IReadOnlyList<Row> rows,
        string? commonName = null,
        string? @event = null,
        AlertLevel level = AlertLevel.Info,
        CancellationToken cancellationToken = default) =>
        ОтправитьAsync(
            server,
            kind,
            title,
            message,
            язык => new MailTable(
                KerioMail.Columns(язык),
                [.. rows.Select(r => (IReadOnlyList<string>)
                    [KerioMail.Label(r.Key, язык), KerioMail.IsPhrase(r.Key) ? texts.Translate(r.Value, язык) : r.Value])]),
            commonName,
            @event,
            level,
            cancellationToken);

    /// <summary>Сводное письмо об очереди установки: таблица имён с итогом.</summary>
    public Task<bool> SendQueueReportAsync(KerioServer server, QueueReport.Summary summary, CancellationToken cancellationToken = default) =>
        ОтправитьAsync(
            server,
            KerioMail.Summary,
            summary.Title,
            summary.Message,
            язык => new MailTable(
                KerioMail.SummaryColumns(язык),
                [.. summary.Rows.Select(r => (IReadOnlyList<string>)
                    [r.CommonName, texts.Translate(r.Outcome, язык), texts.Translate(r.Details, язык)])]),
            "—",
            KerioMail.SummaryEvent,
            summary.Level,
            cancellationToken);

    private async Task<bool> ОтправитьAsync(
        KerioServer? server,
        string kind,
        string title,
        string message,
        Func<string, MailTable> table,
        string? commonName,
        string? @event,
        AlertLevel level,
        CancellationToken cancellationToken)
    {
        var имя = server?.Name ?? "Kerio Cert Manager";

        try
        {
            if (!await mail.CanSendAsync(cancellationToken))
            {
                log.LogInformation("kerio_mail_skipped_not_configured {Server} {Kind}", имя, kind);
                return false;
            }

            var кому = await RecipientsAsync(server, cancellationToken);

            if (кому.Count == 0)
            {
                return false;
            }

            var язык = await settings.GetAsync(KerioSettings.AlertLocale, cancellationToken);

            var значения = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["server"] = имя,
                ["title"] = texts.Translate(title, язык),
                ["message"] = texts.Translate(message, язык),
                ["common_name"] = commonName ?? server?.CommonName ?? "—",
            };

            var таблицы = new Dictionary<string, MailTable>(StringComparer.Ordinal) { [KerioMail.TableName] = table(язык) };
            var отправлено = 0;

            await db.SaveChangesAsync(cancellationToken);

            try
            {
                await db.InTransactionAsync(
                    async token =>
                    {
                        foreach (var адрес in кому)
                        {
                            var итог = await mail.EnqueueAsync(адрес, kind, язык, значения, таблицы, [], cancellationToken: token);

                            if (итог.Result == MailQueueResult.Queued)
                            {
                                отправлено++;
                            }
                        }

                        var сейчас = clock.GetUtcNow();

                        db.Set<KerioNotification>().Add(new KerioNotification
                        {
                            Id = Guid.CreateVersion7(),
                            At = сейчас,
                            ServerId = server?.Id,
                            ServerName = server?.Name,
                            Event = @event ?? kind,
                            Level = Уровень(level),
                            Title = title,
                            Recipients = [.. кому.Take(KerioNotification.MaxRecipients)],
                            Queued = отправлено,
                        });

                        await db.SaveChangesAsync(token);

                        await db.Set<KerioNotification>().Where(о => о.At < сейчас - СрокЖурнала).ExecuteDeleteAsync(token);
                    },
                    cancellationToken);
            }
            finally
            {
                // ⚠ InTransactionAsync очистил трекер: сервер снова под наблюдением, иначе правки вызывающего пропадут.
                if (server is not null && db.Entry(server).State == EntityState.Detached)
                {
                    db.Attach(server);
                }
            }

            log.LogInformation("kerio_mail_queued {Server} {Kind} {Count}", имя, kind, отправлено);

            return отправлено > 0;
        }
        catch (Exception ошибка) when (!(ошибка is OperationCanceledException && cancellationToken.IsCancellationRequested))
        {
            log.LogError("kerio_mail_failed {Server} {Kind} {Error}", имя, kind, SafeError.Describe(ошибка));
            return false;
        }
    }

    /// <summary>Пробное оповещение общим получателям: доходят ли письма и как они выглядят.</summary>
    public async Task<IReadOnlyList<string>> TestAsync(Guid? requestedBy, CancellationToken cancellationToken = default)
    {
        if (!await mail.CanSendAsync(cancellationToken))
        {
            return [];
        }

        var кому = await RecipientsAsync(null, cancellationToken);
        var кто = requestedBy is { } id
            ? await accounts.Query().Where(у => у.Id == id).Select(у => у.Email).FirstOrDefaultAsync(cancellationToken)
            : null;

        var отправлено = await SendAsync(
            null,
            KerioMail.Warning,
            "Проверка оповещений Kerio Cert Manager",
            "Это пробное письмо: так выглядят оповещения о сертификатах на серверах Kerio Connect. Отвечать на него не нужно.",
            [new Row("reason", кто is null ? "Проверка из панели" : $"Проверка из панели: {кто}")],
            "—",
            "test",
            AlertLevel.Info,
            cancellationToken);

        return отправлено ? кому : [];
    }

    /// <summary>
    /// Разослать верные сейчас предупреждения, не чаще <c>kerio.alert_repeat_hours</c>: одно условие — своим письмом,
    /// несколько — одним письмом с таблицей. Ушедшие условия забыть; <paramref name="complete"/> — всё ли проверено
    /// в этом проходе: если нет, молчание условия ничего не значит и его ключ остаётся.
    /// </summary>
    public async Task AlertAsync(KerioServer server, IReadOnlyList<ServerAlert> active, bool complete = true, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(server);
        ArgumentNullException.ThrowIfNull(active);

        var сейчас = clock.GetUtcNow();
        var повтор = TimeSpan.FromHours(await settings.GetAsync(KerioSettings.AlertRepeatHours, cancellationToken));
        var напоминания = Напоминания(await settings.GetAsync(KerioSettings.AlertReminderDays, cancellationToken));

        var включённые = new List<(ServerAlert Условие, string Ключ)>();

        foreach (var условие in active.DistinctBy(а => (а.Kind, Предмет: а.Scope?.ToLowerInvariant())))
        {
            if (!await ВключеноAsync(условие.Kind, cancellationToken))
            {
                continue;
            }

            var ступень = условие.Kind == AlertKinds.Expiring
                ? Ступень(условие.Expires ?? server.ServedNotAfter, напоминания, сейчас)
                : null;

            включённые.Add((условие, Ключ(условие, ступень)));
        }

        var отказы = Ключи.Отказы(server);

        foreach (var вид in server.Alerts.Keys.ToList())
        {
            var ушло = вид.StartsWith(Ключи.Установка, StringComparison.Ordinal)
                ? !отказы.Contains(вид)
                : complete && включённые.All(в2 => в2.Ключ != вид);

            if (ушло)
            {
                server.Alerts.Remove(вид);
            }
        }

        var пора = включённые
            .Where(в => !в.Условие.Quiet
                && !(server.Alerts.TryGetValue(в.Ключ, out var было) && (повтор == TimeSpan.Zero || сейчас - было < повтор)))
            .ToList();

        if (пора.Count == 0)
        {
            return;
        }

        var отправлено = пора.Count == 1
            ? await ОдноAsync(server, пора[0].Условие, cancellationToken)
            : await СводкаAsync(server, [.. пора.Select(в => в.Условие)], cancellationToken);

        if (отправлено)
        {
            foreach (var (_, ключ) in пора)
            {
                server.Alerts[ключ] = сейчас;
            }
        }
    }

    private static string Значок(AlertLevel level) => level switch
    {
        AlertLevel.Critical => "🔴 ",
        AlertLevel.Warning => "🟠 ",
        _ => string.Empty,
    };

    /// <summary>Одно условие — своим письмом.</summary>
    private Task<bool> ОдноAsync(KerioServer server, ServerAlert условие, CancellationToken cancellationToken)
    {
        var строки = new List<Row> { new("server", server.Name), new("address", server.BaseUrl) };

        // Условие о своём имени: «что подаётся по умолчанию» к нему не относится.
        if (условие.Scope is null && server.ServedSubject is not null)
        {
            строки.Add(new Row(
                "served",
                $"{server.ServedSubject} · {server.ServedNotAfter?.ToUniversalTime():dd.MM.yyyy HH:mm} UTC"));
        }

        return SendAsync(
            server, KerioMail.Warning, Значок(условие.Level) + условие.Title, условие.Message, строки, условие.Scope, условие.Kind, условие.Level, cancellationToken);
    }

    /// <summary>⚠ Несколько условий сразу — одним письмом: десятки сертификатов одного сервера не должны давать десятки писем.</summary>
    private Task<bool> СводкаAsync(KerioServer server, IReadOnlyList<ServerAlert> условия, CancellationToken cancellationToken)
    {
        var важнейшее = условия.Max(у => у.Level);
        var порядок = условия.OrderByDescending(у => у.Level).ThenBy(у => у.Scope, StringComparer.OrdinalIgnoreCase).ToList();
        var сколько = условия.Count.ToString(CultureInfo.InvariantCulture);

        return ОтправитьAsync(
            server,
            KerioMail.Warning,
            $"{Значок(важнейшее)}«{server.Name}»: предупреждений — {сколько}",
            "На этом сервере сразу несколько условий требуют внимания. Повторы — не чаще срока из настроек оповещений.",
            язык => new MailTable(
                KerioMail.DigestColumns(язык),
                [.. порядок.Select(у => (IReadOnlyList<string>)[Значок(у.Level) + texts.Translate(у.Title, язык), texts.Translate(у.Message, язык)])]),
            "—",
            KerioMail.DigestEvent,
            важнейшее,
            cancellationToken);
    }

    /// <summary>Ключ подавления повтора: вид, ступень напоминания, предмет.</summary>
    internal static string Ключ(ServerAlert условие, int? ступень) =>
        условие.Kind
        + (ступень is { } дни ? ":" + дни.ToString(CultureInfo.InvariantCulture) : string.Empty)
        + (условие.Scope is { } предмет ? "@" + предмет.ToLowerInvariant() : string.Empty);

    /// <summary>Ключи подавления повторов отчётов об установке.</summary>
    internal static class Ключи
    {
        public const string Установка = "deploy:";

        /// <summary>С именем: короткие серийные внутреннего УЦ совпадают у разных сертификатов.</summary>
        public static string Отказ(string commonName, string serial) =>
            Установка + "failed:" + commonName.ToLowerInvariant() + ":" + serial;

        /// <summary>Ключи выпусков, что сейчас числятся неудачными: остальные больше не нужны.</summary>
        public static HashSet<string> Отказы(KerioServer server)
        {
            var ключи = new HashSet<string>(StringComparer.Ordinal);

            if (server.CommonName is { } имя && server.FailedSerial is { } серийный)
            {
                ключи.Add(Отказ(имя, серийный));
            }

            foreach (var доп in server.Extras)
            {
                if (доп.FailedSerial is { } свой)
                {
                    ключи.Add(Отказ(доп.CommonName, свой));
                }
            }

            return ключи;
        }
    }
}
