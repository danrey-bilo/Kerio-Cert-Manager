using System.Globalization;
using KerioCertManager.Modules.Kerio.Certificates;
using KerioCertManager.Modules.Kerio.Client;
using KerioCertManager.Modules.Kerio.Domain;
using KerioCertManager.Modules.KeyManager.Contracts;

namespace KerioCertManager.Modules.Kerio.Sync;

/// <summary>Важность предупреждения.</summary>
internal enum AlertLevel
{
    Info,
    Warning,
    Critical,
}

/// <summary>
/// Условие, о котором стоит написать администраторам; вид и предмет — ключ подавления повторов. Тихое — верно, но
/// письмо о нём лишнее (сертификат в очереди скоро встанет): счёт повторов идёт дальше.
/// </summary>
internal sealed record ServerAlert(
    string Kind, AlertLevel Level, string Title, string Message, string? Scope = null, DateTimeOffset? Expires = null, bool Quiet = false);

/// <summary>Виды предупреждений.</summary>
internal static class AlertKinds
{
    public const string Expiring = "expiring";
    public const string Expired = "expired";
    public const string SourceMissing = "source_missing";
    public const string Drift = "drift";
    public const string RenewalPending = "renewal_pending";
    public const string Unreachable = "unreachable";
    public const string AuthRejected = "auth_rejected";
    public const string NotAdmin = "not_admin";
    public const string NotTrusted = "not_trusted";

    /// <summary>Продлённый в KMP сертификат не встал на сервер за <c>kerio.alert_overdue_hours</c>.</summary>
    public const string Overdue = "overdue";

    /// <summary>Для имени сервер подаёт не тот сертификат, что лежит в хранилище свежим: Kerio держит прежнюю версию.</summary>
    public const string NameStale = "name_stale";
}

/// <summary>Что делать с сервером в этом проходе.</summary>
internal enum PlanAction
{
    /// <summary>Ничего не менять.</summary>
    None,

    /// <summary>На сервере уже текущий выпуск KMP — записать его установленным, не трогая Kerio.</summary>
    Adopt,

    /// <summary>Поставить текущий выпуск KMP.</summary>
    Deploy,
}

/// <summary>Решение по серверу.</summary>
internal sealed record DeployPlan(
    PlanAction Action,
    string? Trigger,
    SourceCertificate? Source,
    IReadOnlyList<ServerAlert> Alerts,
    bool Behind = false);

/// <summary>Решает, ставить ли сертификат, — без побочных эффектов.</summary>
internal static class DeployPlanner
{
    /// <summary>Календарных дней (UTC) до конца срока — одно число в панели и письмах; истёкший сегодня — уже −1.</summary>
    internal static int ДнейДо(DateTimeOffset notAfter, DateTimeOffset now)
    {
        var дней = (notAfter.UtcDateTime.Date - now.UtcDateTime.Date).Days;

        return notAfter < now ? Math.Min(дней, -1) : дней;
    }

    public static DeployPlan Plan(
        KerioServer server,
        ServedCertificate? served,
        SourceCertificate? source,
        bool globalAutoDeploy,
        int warningDays,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(server);

        var ручной = server.ManualFor(server.CommonName);
        var предупреждения = new List<ServerAlert>();

        if (string.IsNullOrWhiteSpace(server.CommonName))
        {
            Срок(served, warningDays, now, предупреждения, вKmpЕстьНовый: false);
            return new DeployPlan(PlanAction.None, null, null, предупреждения);
        }

        if (source is null)
        {
            предупреждения.Add(new ServerAlert(
                AlertKinds.SourceMissing,
                AlertLevel.Warning,
                $"В Key Manager Plus нет сертификата {server.CommonName}",
                $"Сервер «{server.Name}» привязан к сертификату {server.CommonName}, но последний опрос Key Manager Plus его "
                + "не нашёл. Проверьте имя в настройках сервера или сертификат в Key Manager Plus."));

            Срок(served, warningDays, now, предупреждения, вKmpЕстьНовый: false);

            return new DeployPlan(PlanAction.None, null, null, предупреждения);
        }

        var наМесте = served is not null && Fingerprints.SameSerial(served.Serial, source.SerialNumber);

        // ⚠ Ручной запрос не гасится тем, что порт администрирования уже подаёт текущий выпуск.
        if (наМесте && !ручной)
        {
            Срок(served, warningDays, now, предупреждения, вKmpЕстьНовый: false);

            // ⚠ «Свой» — по отпечатку, а не только по серийному: короткие серийные внутреннего УЦ совпадают у чужих.
            var свой = Fingerprints.SameSerial(server.InstalledSerial, source.SerialNumber)
                && (server.InstalledSha256 is null || string.Equals(server.InstalledSha256, served!.Sha256, StringComparison.Ordinal));

            return new DeployPlan(свой ? PlanAction.None : PlanAction.Adopt, null, source, предупреждения);
        }

        if (ручной)
        {
            Срок(served, warningDays, now, предупреждения, вKmpЕстьНовый: !наМесте);

            return new DeployPlan(PlanAction.Deploy, DeployTriggers.Manual, source, предупреждения, Behind: !наМесте);
        }

        if (served is null)
        {
            return new DeployPlan(
                PlanAction.None,
                null,
                source,
                предупреждения,
                Behind: server.ServedSerial is not null && !Fingerprints.SameSerial(server.ServedSerial, source.SerialNumber)
                    && !Fingerprints.SameSerial(server.InstalledSerial, source.SerialNumber));
        }

        if (Fingerprints.SameSerial(server.InstalledSerial, source.SerialNumber))
        {
            предупреждения.Add(new ServerAlert(
                AlertKinds.Drift,
                AlertLevel.Warning,
                $"На «{server.Name}» стоит не тот сертификат, что установил сервис",
                $"Сервер подаёт сертификат {served.Subject} (серийный {served.Serial}, до {Дата(served.NotAfter)}), а сервис "
                + $"устанавливал {source.CommonName} (серийный {Fingerprints.Serial(source.SerialNumber)}). Если это не намеренно, "
                + "нажмите «Обновить» у сертификата во вкладке «Обзор»."));

            Срок(served, warningDays, now, предупреждения, вKmpЕстьНовый: false);

            return new DeployPlan(PlanAction.None, null, source, предупреждения);
        }

        var первая = server.InstalledSerial is null;
        var истекает = served.NotAfter - now <= TimeSpan.FromDays(warningDays);

        if (ЧужойДольше(server, served.Serial, served.NotAfter, !served.SelfSigned && served.ChainTrusted && НаИмя(served, source.CommonName), source))
        {
            Срок(served, warningDays, now, предупреждения, вKmpЕстьНовый: false);
            return new DeployPlan(PlanAction.None, null, source, предупреждения);
        }

        var триггер = первая ? DeployTriggers.Initial : истекает ? DeployTriggers.Expiring : DeployTriggers.Renewal;

        if (!InstallPolicy.Automatic(globalAutoDeploy, server.AutoDeploy, server.InstalledSerial))
        {
            предупреждения.Add(new ServerAlert(
                AlertKinds.RenewalPending,
                истекает ? AlertLevel.Critical : AlertLevel.Warning,
                $"Для «{server.Name}» есть новый сертификат — ждёт установки",
                $"В Key Manager Plus выпуск {source.CommonName} (серийный {Fingerprints.Serial(source.SerialNumber)}"
                + (source.ExpiresOn is { } срок ? $", до {срок:dd.MM.yyyy}" : string.Empty)
                + "), а автоматическая установка "
                + (globalAutoDeploy ? "для этого сервера" : "во всём сервисе")
                + " выключена. Установите его кнопкой «Обновить» у сертификата во вкладке «Обзор»."));

            Срок(served, warningDays, now, предупреждения, вKmpЕстьНовый: true);

            return new DeployPlan(PlanAction.None, null, source, предупреждения, Behind: true);
        }

        if (Fingerprints.SameSerial(server.FailedSerial, source.SerialNumber) && server.NextAttemptAt is { } повтор && повтор > now)
        {
            Срок(served, warningDays, now, предупреждения, вKmpЕстьНовый: true);

            return new DeployPlan(PlanAction.None, null, source, предупреждения, Behind: true);
        }

        if (server.DeployFailures > 0 && Fingerprints.SameSerial(server.FailedSerial, source.SerialNumber))
        {
            триггер = DeployTriggers.Retry;
        }

        Срок(served, warningDays, now, предупреждения, вKmpЕстьНовый: true);

        return new DeployPlan(PlanAction.Deploy, триггер, source, предупреждения, Behind: true);
    }

    /// <summary>
    /// Подаётся доверенный сертификат на это имя, не поставленный сервисом, и действует дольше выпуска KMP: сам сервис
    /// «хуже, чем было» не делает. Правило одно для сертификата по умолчанию и сертификатов имён.
    /// </summary>
    internal static bool ЧужойДольше(KerioServer server, string? servedSerial, DateTimeOffset servedNotAfter, bool trustedForName, SourceCertificate source) =>
        trustedForName
        && !server.Installed(servedSerial)
        && !Fingerprints.SameSerial(servedSerial, source.SerialNumber)
        && source.ExpiresOn is { } до
        && servedNotAfter > new DateTimeOffset(до.ToDateTime(TimeOnly.MaxValue), TimeSpan.Zero);

    /// <summary>
    /// Пауза перед повтором после <paramref name="failures"/> неудач подряд: 5, 15, 30 минут, час, 3, 6, 12 часов, затем
    /// сутки. ⚠ Неудачный повтор — это перезапуск HTTPS на рабочем сервере: долго не получается — не дёргать его часто.
    /// </summary>
    public static TimeSpan Backoff(int failures) => failures switch
    {
        <= 1 => TimeSpan.FromMinutes(5),
        2 => TimeSpan.FromMinutes(15),
        3 => TimeSpan.FromMinutes(30),
        4 => TimeSpan.FromHours(1),
        5 => TimeSpan.FromHours(3),
        6 => TimeSpan.FromHours(6),
        7 => TimeSpan.FromHours(12),
        _ => TimeSpan.FromHours(24),
    };

    private static bool НаИмя(ServedCertificate served, string commonName) =>
        served.DnsNames.Append(served.Subject).Any(и => Fingerprints.NameCovers(и, commonName));

    /// <summary>Предупреждения о сроке поданного сертификата, если заменить его сейчас нечем.</summary>
    private static void Срок(
        ServedCertificate? served, int warningDays, DateTimeOffset now, List<ServerAlert> предупреждения, bool вKmpЕстьНовый)
    {
        if (served is null)
        {
            return;
        }

        var осталось = served.NotAfter - now;

        if (осталось <= TimeSpan.Zero)
        {
            предупреждения.Add(new ServerAlert(
                AlertKinds.Expired,
                AlertLevel.Critical,
                $"Сертификат {served.Subject} истёк",
                $"Сервер подаёт сертификат {served.Subject}, истёкший {Дата(served.NotAfter)}. Клиенты получают ошибку "
                + "доверия. " + (вKmpЕстьНовый ? "Новый выпуск в Key Manager Plus ждёт установки." : "Выпустите новый в Key Manager Plus.")));
        }
        else if (осталось <= TimeSpan.FromDays(warningDays))
        {
            var дней = Math.Max(ДнейДо(served.NotAfter, now), 1);

            предупреждения.Add(new ServerAlert(
                AlertKinds.Expiring,
                дней <= 7 ? AlertLevel.Critical : AlertLevel.Warning,
                $"Сертификат {served.Subject} истекает через {дней.ToString(CultureInfo.InvariantCulture)} дн.",
                $"Сервер подаёт сертификат {served.Subject}, действующий до {Дата(served.NotAfter)}. "
                + (вKmpЕстьНовый
                    ? "Новый выпуск в Key Manager Plus ждёт установки."
                    : "В Key Manager Plus нового выпуска пока нет — продлите сертификат.")));
        }
    }

    private static string Дата(DateTimeOffset когда) =>
        когда.ToUniversalTime().ToString("dd.MM.yyyy HH:mm 'UTC'", CultureInfo.InvariantCulture);
}
