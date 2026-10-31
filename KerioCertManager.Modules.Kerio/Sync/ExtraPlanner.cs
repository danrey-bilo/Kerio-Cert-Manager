using System.Globalization;
using KerioCertManager.Modules.Kerio.Certificates;
using KerioCertManager.Modules.Kerio.Client;
using KerioCertManager.Modules.Kerio.Domain;
using KerioCertManager.Modules.KeyManager.Contracts;

namespace KerioCertManager.Modules.Kerio.Sync;

/// <summary>Решение по сертификату своего имени.</summary>
internal sealed record ExtraPlan(
    PlanAction Action,
    string? Trigger,
    SourceCertificate? Source,
    StoredCertificate? Present,
    IReadOnlyList<ServerAlert> Alerts,
    bool Behind);

/// <summary>Решает, ставить ли сертификат своего имени в хранилище Kerio, — без побочных эффектов.</summary>
internal static class ExtraPlanner
{
    public static ExtraPlan Plan(
        KerioServer server,
        ExtraCertificate extra,
        SourceCertificate? source,
        bool storeKnown,
        bool globalAutoDeploy,
        int warningDays,
        DateTimeOffset now,
        bool manual)
    {
        ArgumentNullException.ThrowIfNull(server);
        ArgumentNullException.ThrowIfNull(extra);

        var имя = extra.CommonName;
        var предупреждения = new List<ServerAlert>();
        var версии = server.Store.Where(з => Того(з, имя)).OrderByDescending(з => з.NotAfter ?? DateTimeOffset.MinValue).ToList();

        if (source is null)
        {
            предупреждения.Add(new ServerAlert(
                AlertKinds.SourceMissing,
                AlertLevel.Warning,
                $"В Key Manager Plus нет сертификата {имя}",
                $"Сервер «{server.Name}» держит сертификат {имя}, но последний опрос Key Manager Plus его не нашёл. Проверьте "
                + "имя в настройках сервера или сертификат в Key Manager Plus.",
                имя));

            Срок(server, имя, версии.FirstOrDefault(), warningDays, now, предупреждения, вKmpЕстьНовый: false);

            return new ExtraPlan(PlanAction.None, null, null, null, предупреждения, false);
        }

        if (!storeKnown)
        {
            return new ExtraPlan(PlanAction.None, null, source, null, предупреждения, false);
        }

        var лежит = версии.FirstOrDefault(з => Fingerprints.SameSerial(з.Serial, source.SerialNumber));

        if (лежит is not null)
        {
            Срок(server, имя, лежит, warningDays, now, предупреждения, вKmpЕстьНовый: false);
            Подаётся(server, имя, лежит, предупреждения);

            var свой = Fingerprints.SameSerial(extra.InstalledSerial, source.SerialNumber)
                && string.Equals(extra.InstalledSha256, лежит.Sha256, StringComparison.Ordinal);

            return new ExtraPlan(свой ? PlanAction.None : PlanAction.Adopt, null, source, лежит, предупреждения, false);
        }

        if (!manual && ЧужойДольше(server, extra, source) is { } дольше)
        {
            Срок(server, имя, дольше, warningDays, now, предупреждения, вKmpЕстьНовый: false);
            return new ExtraPlan(PlanAction.None, null, source, null, предупреждения, false);
        }

        Срок(server, имя, версии.FirstOrDefault(), warningDays, now, предупреждения, вKmpЕстьНовый: true);

        if (manual)
        {
            return new ExtraPlan(PlanAction.Deploy, DeployTriggers.Manual, source, null, предупреждения, true);
        }

        if (!globalAutoDeploy || !server.AutoDeploy)
        {
            предупреждения.Add(new ServerAlert(
                AlertKinds.RenewalPending,
                AlertLevel.Warning,
                $"Для «{server.Name}» есть новый сертификат {имя} — ждёт установки",
                $"В Key Manager Plus выпуск {имя} (серийный {Fingerprints.Serial(source.SerialNumber)}"
                + (source.ExpiresOn is { } срок ? $", до {срок:dd.MM.yyyy}" : string.Empty)
                + "), а автоматическая установка " + (globalAutoDeploy ? "для этого сервера" : "во всём сервисе")
                + " выключена. Установите его кнопкой «Установить сейчас» в настройках сервера.",
                имя));

            return new ExtraPlan(PlanAction.None, null, source, null, предупреждения, true);
        }

        if (Fingerprints.SameSerial(extra.FailedSerial, source.SerialNumber) && extra.NextAttemptAt is { } повтор && повтор > now)
        {
            return new ExtraPlan(PlanAction.None, null, source, null, предупреждения, true);
        }

        var триггер = extra.Failures > 0 && Fingerprints.SameSerial(extra.FailedSerial, source.SerialNumber)
            ? DeployTriggers.Retry
            : extra.InstalledSerial is null && версии.Count == 0
                ? DeployTriggers.Initial
                : DeployTriggers.Renewal;

        return new ExtraPlan(PlanAction.Deploy, триггер, source, null, предупреждения, true);
    }

    /// <summary>Доверенная версия имени не из KMP (не текущий и ни один поставленный сервисом выпуск), что действует дольше выпуска KMP.</summary>
    internal static StoredCertificate? ЧужойДольше(KerioServer server, ExtraCertificate extra, SourceCertificate source)
    {
        if (source.ExpiresOn is not { } до)
        {
            return null;
        }

        var конецKmp = new DateTimeOffset(до.ToDateTime(TimeOnly.MaxValue), TimeSpan.Zero);

        return server.Store
            .Where(з => Того(з, extra.CommonName)
                && !з.Untrusted
                && !з.SelfSigned
                && з.NotAfter > конецKmp
                && !Fingerprints.SameSerial(з.Serial, source.SerialNumber)
                && !extra.Installed(з.Serial))
            .OrderByDescending(з => з.NotAfter)
            .FirstOrDefault();
    }

    /// <summary>Сертификат хранилища — версия этого имени: по субъекту, как его показывает Kerio.</summary>
    internal static bool Того(StoredCertificate з, string commonName) =>
        string.Equals(з.Subject, commonName, StringComparison.OrdinalIgnoreCase);

    /// <summary>Для имени подаётся не та версия, что лежит свежей, — Kerio держит прежнюю.</summary>
    private static void Подаётся(KerioServer server, string commonName, StoredCertificate лежит, List<ServerAlert> предупреждения)
    {
        if (Sni.For(commonName) is not { } имя
            || лежит.Sha256 is null
            || !server.ServedByName.TryGetValue(имя, out var подан)
            || string.Equals(подан, лежит.Sha256, StringComparison.Ordinal))
        {
            return;
        }

        var что = server.Store.FirstOrDefault(з => string.Equals(з.Sha256, подан, StringComparison.Ordinal));

        предупреждения.Add(new ServerAlert(
            AlertKinds.NameStale,
            AlertLevel.Critical,
            $"«{server.Name}» подаёт для {имя} не новый сертификат",
            $"Свежий выпуск {commonName} лежит в хранилище Kerio, а для имени {имя} сервер подаёт "
            + (что is null ? "другой сертификат" : $"«{что.Subject}» до {что.NotAfter?.ToUniversalTime():dd.MM.yyyy}")
            + ". Kerio подхватывает новую версию после удаления прежней или перезапуска службы Kerio Connect.",
            commonName));
    }

    private static void Срок(
        KerioServer server,
        string commonName,
        StoredCertificate? версия,
        int warningDays,
        DateTimeOffset now,
        List<ServerAlert> предупреждения,
        bool вKmpЕстьНовый)
    {
        if (версия?.NotAfter is not { } до)
        {
            return;
        }

        var осталось = до - now;
        var когда = до.ToUniversalTime().ToString("dd.MM.yyyy HH:mm 'UTC'", CultureInfo.InvariantCulture);

        if (осталось <= TimeSpan.Zero)
        {
            предупреждения.Add(new ServerAlert(
                AlertKinds.Expired,
                AlertLevel.Critical,
                $"Сертификат {commonName} на «{server.Name}» истёк",
                $"В хранилище Kerio сертификат {commonName} истёк {когда}. Клиенты, которые обращаются по этому имени, получают "
                + "ошибку доверия. " + (вKmpЕстьНовый ? "Новый выпуск в Key Manager Plus ждёт установки." : "Выпустите новый в Key Manager Plus."),
                commonName,
                до));
        }
        else if (осталось <= TimeSpan.FromDays(warningDays))
        {
            var дней = (int)Math.Ceiling(осталось.TotalDays);

            предупреждения.Add(new ServerAlert(
                AlertKinds.Expiring,
                дней <= 7 ? AlertLevel.Critical : AlertLevel.Warning,
                $"Сертификат {commonName} на «{server.Name}» истекает через {дней.ToString(CultureInfo.InvariantCulture)} дн.",
                $"Сертификат {commonName} в хранилище Kerio действует до {когда}. "
                + (вKmpЕстьНовый ? "Новый выпуск в Key Manager Plus ждёт установки." : "В Key Manager Plus нового выпуска пока нет — продлите сертификат."),
                commonName,
                до));
        }
    }
}
