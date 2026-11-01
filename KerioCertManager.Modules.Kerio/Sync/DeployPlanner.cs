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

/// <summary>Условие, о котором стоит написать администраторам; вид и предмет — ключ подавления повторов.</summary>
internal sealed record ServerAlert(
    string Kind, AlertLevel Level, string Title, string Message, string? Scope = null, DateTimeOffset? Expires = null);

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
    string Note,
    bool ClearManualRequest,
    bool Behind = false);

/// <summary>Решает, ставить ли сертификат, — без побочных эффектов.</summary>
internal static class DeployPlanner
{
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
            return new DeployPlan(PlanAction.None, null, null, предупреждения, "Сертификат Key Manager Plus не выбран — только наблюдение.", ручной);
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

            return new DeployPlan(PlanAction.None, null, null, предупреждения, "В Key Manager Plus нет привязанного сертификата.", ручной);
        }

        var наМесте = served is not null && Fingerprints.SameSerial(served.Serial, source.SerialNumber);

        // ⚠ Ручной запрос не гасится тем, что порт администрирования уже подаёт текущий выпуск.
        if (наМесте && !ручной)
        {
            Срок(served, warningDays, now, предупреждения, вKmpЕстьНовый: false);

            // ⚠ «Свой» — по отпечатку, а не только по серийному: короткие серийные внутреннего УЦ совпадают у чужих.
            var свой = Fingerprints.SameSerial(server.InstalledSerial, source.SerialNumber)
                && (server.InstalledSha256 is null || string.Equals(server.InstalledSha256, served!.Sha256, StringComparison.Ordinal));

            return new DeployPlan(
                свой ? PlanAction.None : PlanAction.Adopt,
                null,
                source,
                предупреждения,
                свой ? "На сервере текущий сертификат." : "На сервере уже текущий сертификат Key Manager Plus — принят как установленный.",
                ручной);
        }

        if (ручной)
        {
            return new DeployPlan(
                PlanAction.Deploy, DeployTriggers.Manual, source, предупреждения, "Установка по запросу администратора.", true, Behind: !наМесте);
        }

        if (served is null)
        {
            return new DeployPlan(
                PlanAction.None,
                null,
                source,
                предупреждения,
                "Сервер не ответил по TLS — установка отложена.",
                false,
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

            return new DeployPlan(PlanAction.None, null, source, предупреждения, "Сертификат на сервере сменили мимо сервиса.", false);
        }

        var первая = server.InstalledSerial is null;
        var истекает = served.NotAfter - now <= TimeSpan.FromDays(warningDays);

        if (первая)
        {
            var дольше = source.ExpiresOn is not { } до || до > DateOnly.FromDateTime(served.NotAfter.UtcDateTime);
            var чужой = served.SelfSigned || !served.ChainTrusted || !НаИмя(served, source.CommonName);

            if (!дольше && !истекает && !чужой)
            {
                return new DeployPlan(
                    PlanAction.None,
                    null,
                    source,
                    предупреждения,
                    "На сервере действует сертификат дольше выпуска из Key Manager Plus — замена не нужна.",
                    false);
            }
        }

        var триггер = первая ? DeployTriggers.Initial : истекает ? DeployTriggers.Expiring : DeployTriggers.Renewal;

        if (!globalAutoDeploy || !server.AutoDeploy)
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

            return new DeployPlan(PlanAction.None, null, source, предупреждения, "Новый сертификат ждёт ручной установки.", false, Behind: true);
        }

        if (Fingerprints.SameSerial(server.FailedSerial, source.SerialNumber) && server.NextAttemptAt is { } повтор && повтор > now)
        {
            Срок(served, warningDays, now, предупреждения, вKmpЕстьНовый: true);

            return new DeployPlan(
                PlanAction.None,
                null,
                source,
                предупреждения,
                $"Прошлая установка не удалась; повтор после {повтор.ToUniversalTime():yyyy-MM-dd HH:mm} UTC.",
                false,
                Behind: true);
        }

        if (server.DeployFailures > 0 && Fingerprints.SameSerial(server.FailedSerial, source.SerialNumber))
        {
            триггер = DeployTriggers.Retry;
        }

        return new DeployPlan(PlanAction.Deploy, триггер, source, предупреждения, "Новый выпуск в Key Manager Plus — установка.", false, Behind: true);
    }

    /// <summary>Пауза перед повтором после <paramref name="failures"/> неудач подряд: 5, 15, 30 минут, затем час.</summary>
    public static TimeSpan Backoff(int failures) => failures switch
    {
        <= 1 => TimeSpan.FromMinutes(5),
        2 => TimeSpan.FromMinutes(15),
        3 => TimeSpan.FromMinutes(30),
        _ => TimeSpan.FromHours(1),
    };

    private static bool НаИмя(ServedCertificate served, string commonName)
    {
        var имя = commonName.Trim().ToLowerInvariant();

        return served.DnsNames.Append(served.Subject).Any(и =>
            string.Equals(и.Trim().ToLowerInvariant(), имя, StringComparison.Ordinal));
    }

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
            var дней = (int)Math.Ceiling(осталось.TotalDays);

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
