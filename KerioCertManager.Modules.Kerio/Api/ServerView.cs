using KerioCertManager.Modules.Kerio.Certificates;
using KerioCertManager.Modules.Kerio.Client;
using KerioCertManager.Modules.Kerio.Domain;
using KerioCertManager.Modules.Kerio.Sync;
using KerioCertManager.Modules.KeyManager.Contracts;

namespace KerioCertManager.Modules.Kerio.Api;

/// <summary>Состояния сервера для панели — по важности.</summary>
internal static class ServerStatuses
{
    public const string Disabled = "disabled";
    public const string Unchecked = "unchecked";
    public const string Error = "error";
    public const string Expired = "expired";
    public const string DeployFailed = "deploy_failed";
    public const string UpdatePending = "update_pending";
    public const string Expiring = "expiring";
    public const string Unbound = "unbound";
    public const string Ok = "ok";

    /// <summary>Свежий лежит в хранилище, а для имени Kerio подаёт прежний.</summary>
    public const string NameStale = "name_stale";
}

/// <summary>Строка сервера → ответ панели со статусом.</summary>
internal static class ServerView
{
    public static ServerOut Of(
        KerioServer с,
        IReadOnlyDictionary<string, SourceCertificate> kmp,
        int warningDays,
        DateTimeOffset now,
        bool dryRun = false,
        IReadOnlyDictionary<string, SourceCertificate>? bySerial = null,
        bool paused = false,
        IReadOnlyList<string>? requested = null,
        string? installing = null)
    {
        ArgumentNullException.ThrowIfNull(с);
        ArgumentNullException.ThrowIfNull(kmp);

        var очередь = Очередь(с, requested ?? [], installing);

        bool ВОчереди(string? имя) => имя is not null
            && (очередь.Contains(имя, StringComparer.OrdinalIgnoreCase) || string.Equals(имя, installing, StringComparison.OrdinalIgnoreCase));

        var выпуск = с.CommonName is { } имя && kmp.TryGetValue(имя, out var найден) ? найден : null;
        var (статус, текст) = Status(с, выпуск, warningDays, now, dryRun, paused, ВОчереди(с.CommonName));

        int? дней = с.ServedNotAfter is { } до ? DeployPlanner.ДнейДо(до, now) : null;

        return new ServerOut(
            с.Id,
            с.Name,
            с.BaseUrl,
            с.Username,
            с.Password is not null,
            с.Enabled,
            с.AutoDeploy,
            с.CleanupOld,
            с.VerifyPorts,
            с.NotifyEmails,
            с.TlsMode,
            с.TlsServerName,
            с.TlsPinSha256,
            с.CommonName,
            статус,
            текст,
            с.LeaseUntil is { } аренда && аренда > now,
            с.LastCheckAt,
            с.LastCheckError,
            с.AuthRejected,
            с.KerioVersion,
            с.KerioRole,
            с.KerioCertificateCount,
            с.ServedSha256 is null
                ? null
                : new ServedOut(с.ServedSha256, с.ServedSubject, с.ServedIssuer, с.ServedSerial, с.ServedNotBefore, с.ServedNotAfter, дней),
            с.InstalledSerial is null
                ? null
                : new InstalledOut(с.InstalledSerial, с.InstalledSha256, с.InstalledNotBefore, с.InstalledNotAfter, с.InstalledAt, с.InstalledKerioId),
            выпуск is null ? null : new SourceOut(выпуск.CommonName, выпуск.SerialNumber, выпуск.ExpiresOn, выпуск.FirstSeenAt),
            с.LastDeployAt,
            с.LastDeployError,
            с.NextAttemptAt,
            очередь,
            installing,
            с.CreatedAt,
            с.UpdatedAt,
            bySerial is null ? [] : Хранилище(с, bySerial, kmp, warningDays, now),
            с.StoreCheckedAt,
            [.. с.Extras.Select(д => Доп(с, д, kmp, warningDays, now, dryRun, paused, ВОчереди(д.CommonName)))],
            с.ServedByName);
    }

    /// <summary>Очередь установки для панели: нажатые и ещё не взятые проходом — первыми, как их возьмёт следующий проход.</summary>
    internal static IReadOnlyList<string> Очередь(KerioServer с, IReadOnlyList<string> requested, string? installing)
    {
        var ведомые = с.ManagedNames();
        var итог = new List<string>();

        foreach (var имя in requested.Concat(с.InstallQueue))
        {
            if (ведомые.Contains(имя, StringComparer.OrdinalIgnoreCase)
                && !string.Equals(имя, installing, StringComparison.OrdinalIgnoreCase)
                && !итог.Contains(имя, StringComparer.OrdinalIgnoreCase))
            {
                итог.Add(имя);
            }
        }

        return итог;
    }

    /// <summary>Сертификат своего имени: что в KMP, лежит ли в хранилище, подаётся ли по имени.</summary>
    internal static ExtraOut Доп(
        KerioServer с, ExtraCertificate д, IReadOnlyDictionary<string, SourceCertificate> kmp, int warningDays, DateTimeOffset now, bool dryRun,
        bool paused = false, bool queued = false)
    {
        var выпуск = kmp.TryGetValue(д.CommonName, out var найден) ? найден : null;
        var лежит = выпуск is null
            ? null
            : с.Store.FirstOrDefault(з => ExtraPlanner.Того(з, д.CommonName) && Fingerprints.SameSerial(з.Serial, выпуск.SerialNumber));
        var дольше = выпуск is null || лежит is not null ? null : ExtraPlanner.ЧужойДольше(с, д, выпуск);
        bool? поИмени = лежит?.Sha256 is { } sha && Sni.For(д.CommonName) is { } имя && с.ServedByName.TryGetValue(имя, out var подан)
            ? string.Equals(подан, sha, StringComparison.Ordinal)
            : null;

        var (статус, текст) =
            !с.Enabled ? (ServerStatuses.Disabled, "Сервер отключён.")
            : выпуск is null ? (ServerStatuses.Error, "Нет в последнем опросе Key Manager Plus.")
            : с.StoreCheckedAt is null ? (ServerStatuses.Unchecked, "Хранилище Kerio ещё не прочитано — проверка в ближайшем проходе.")
            : лежит is not null && поИмени == false
                ? (ServerStatuses.NameStale, "Свежий выпуск в хранилище, но для этого имени Kerio подаёт прежний — перезапустите службу Kerio Connect.")
            : лежит?.NotAfter is { } до && до <= now ? (ServerStatuses.Expired, $"Истёк {до.ToUniversalTime():dd.MM.yyyy}.")
            : лежит?.NotAfter is { } срок && срок - now <= TimeSpan.FromDays(warningDays)
                ? (ServerStatuses.Expiring, $"Истекает {срок.ToUniversalTime():dd.MM.yyyy}, нового выпуска пока нет.")
            : лежит is not null ? (ServerStatuses.Ok, "Текущий выпуск лежит в хранилище Kerio.")
            : дольше is not null
                ? (ServerStatuses.Ok, $"Действует сертификат не из KMP до {дольше.NotAfter?.ToUniversalTime():dd.MM.yyyy} — дольше выпуска KMP; замена не нужна.")
            : queued ? (ServerStatuses.UpdatePending, "Новый выпуск — в очереди на установку.")
            : д.LastError is { } отказ && Fingerprints.SameSerial(д.FailedSerial, выпуск.SerialNumber) ? (ServerStatuses.DeployFailed, отказ)
            : (ServerStatuses.UpdatePending, dryRun
                ? "Новый выпуск — пробный режим: проверен, но в Kerio не ставится."
                : InstallPolicy.Automatic(!paused, с.AutoDeploy, д.InstalledSerial)
                    ? "Новый выпуск — в очереди на установку." : "Новый выпуск — ждёт ручной установки.");

        return new ExtraOut(
            д.CommonName,
            статус,
            текст,
            выпуск is null ? null : new SourceOut(выпуск.CommonName, выпуск.SerialNumber, выпуск.ExpiresOn, выпуск.FirstSeenAt),
            д.InstalledSerial,
            д.InstalledNotAfter,
            д.InstalledAt,
            д.KerioId,
            поИмени,
            д.LastError,
            д.NextAttemptAt,
            д.AddedAt);
    }

    /// <summary>Хранилище Kerio для обзора: что подаётся, откуда каждый сертификат, сколько ему осталось.</summary>
    internal static IReadOnlyList<StoreCertOut> Хранилище(
        KerioServer с, IReadOnlyList<SourceCertificate> releases, int warningDays, DateTimeOffset now) =>
        Хранилище(с, BySerial(releases), SourceReleases.Current(releases), warningDays, now);

    /// <summary>
    /// Выпуски KMP по имени и серийному номеру — один раз на запрос, а не на каждый сервер. ⚠ С именем: короткие
    /// серийные внутреннего УЦ совпадают у разных сертификатов.
    /// </summary>
    public static IReadOnlyDictionary<string, SourceCertificate> BySerial(IReadOnlyList<SourceCertificate> releases) =>
        releases
            .Select(в => (Ключ: КлючВыпуска(в.CommonName, в.SerialNumber), Выпуск: в))
            .GroupBy(п => п.Ключ, StringComparer.Ordinal)
            .ToDictionary(г => г.Key, г => г.First().Выпуск, StringComparer.Ordinal);

    private static string КлючВыпуска(string commonName, string serial) =>
        commonName.ToLowerInvariant() + "|" + Fingerprints.Serial(serial);

    /// <summary>Поданный сертификат по умолчанию — чужой и действует дольше выпуска KMP; доверие и имя — из хранилища.</summary>
    private static bool ПоУмолчаниюДольше(KerioServer с, SourceCertificate выпуск)
    {
        var подан = с.Store.FirstOrDefault(з => string.Equals(з.Sha256, с.ServedSha256, StringComparison.Ordinal));

        return подан is not null && с.ServedNotAfter is { } до && DeployPlanner.ЧужойДольше(
            с,
            с.ServedSerial,
            до,
            !подан.SelfSigned && !подан.Untrusted && подан.DnsNames.Append(подан.Subject).Any(и => Fingerprints.NameCovers(и, выпуск.CommonName)),
            выпуск);
    }

    /// <summary>
    /// Стоит ли кнопка у имени: текущего выпуска нет там, где он должен быть, и замена не хуже того, что есть. Сертификат
    /// по умолчанию должен подаваться, сертификат имени — лежать в хранилище.
    /// </summary>
    private static bool НужнаКнопка(KerioServer с, string имя, SourceCertificate свежий)
    {
        if (string.Equals(имя, с.CommonName, StringComparison.OrdinalIgnoreCase))
        {
            return !Fingerprints.SameSerial(с.ServedSerial, свежий.SerialNumber) && !ПоУмолчаниюДольше(с, свежий);
        }

        return !с.Store.Any(з => ExtraPlanner.Того(з, имя) && Fingerprints.SameSerial(з.Serial, свежий.SerialNumber))
            && с.Extras.FirstOrDefault(д => string.Equals(д.CommonName, имя, StringComparison.OrdinalIgnoreCase)) is { } доп
            && ExtraPlanner.ЧужойДольше(с, доп, свежий) is null;
    }

    internal static IReadOnlyList<StoreCertOut> Хранилище(
        KerioServer с,
        IReadOnlyDictionary<string, SourceCertificate> поНомеру,
        IReadOnlyDictionary<string, SourceCertificate> текущие,
        int warningDays,
        DateTimeOffset now)
    {
        var ведомые = с.ManagedNames().ToHashSet(StringComparer.OrdinalIgnoreCase);
        var подаются = с.ServedByName.Values.Append(с.ServedSha256).OfType<string>().ToHashSet(StringComparer.Ordinal);

        // Самый поздний срок каждого имени: версия раньше него, которую Kerio не подаёт, заменена и в сводку не идёт.
        var новейший = с.Store
            .Where(з => з.NotAfter is not null)
            .GroupBy(з => з.Subject, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(г => г.Key, г => г.Max(з => з.NotAfter!.Value), StringComparer.OrdinalIgnoreCase);

        return [.. с.Store
            .Select(з =>
            {
                var выпуск = з.Serial is { } номер && поНомеру.TryGetValue(КлючВыпуска(з.Subject, номер), out var найден) ? найден : null;
                var свой = (з.Sha256 is not null && string.Equals(з.Sha256, с.InstalledSha256, StringComparison.Ordinal))
                    || (выпуск is not null && ведомые.Contains(выпуск.CommonName))
                    || (с.CommonName is { } имя && ExtraPlanner.Того(з, имя) && с.Installed(з.Serial))
                    || с.Extras.Any(д => ExtraPlanner.Того(з, д.CommonName) && д.Installed(з.Serial));

                var откуда = свой ? "managed"
                    : выпуск is not null ? "kmp"
                    : з.BuiltIn ? "builtin"
                    : з.Type.Contains("Request", StringComparison.OrdinalIgnoreCase) ? "request"
                    : "foreign";

                int? дней = з.NotAfter is { } до ? DeployPlanner.ДнейДо(до, now) : null;

                var срок = з.NotAfter is not { } конец ? "unknown"
                    : конец <= now ? "expired"
                    : конец - now <= TimeSpan.FromDays(warningDays) ? "expiring"
                    : "ok";

                var заменён = (з.Sha256 is null || !подаются.Contains(з.Sha256))
                    && з.NotAfter is { } мой
                    && новейший.TryGetValue(з.Subject, out var свежее)
                    && свежее > мой;

                // ⚠ «Обновить» — только у выбранного для сервера имени и одна на имя: у самой новой его версии.
                var обновить = !заменён
                    && откуда is not ("builtin" or "request")
                    && ведомые.Contains(з.Subject)
                    && (з.NotAfter is not { } срокВерсии || !новейший.TryGetValue(з.Subject, out var поздний) || срокВерсии >= поздний)
                    && текущие.TryGetValue(з.Subject, out var свежий)
                    && НужнаКнопка(с, з.Subject, свежий)
                    ? свежий.CommonName
                    : null;

                return new StoreCertOut(
                    з.Id,
                    з.Type,
                    з.Subject,
                    з.Issuer,
                    з.NotBefore,
                    з.NotAfter,
                    дней,
                    з.Serial,
                    з.Sha256,
                    з.DnsNames,
                    з.SelfSigned,
                    з.Untrusted,
                    з.Sha256 is not null && string.Equals(з.Sha256, с.ServedSha256, StringComparison.Ordinal),
                    откуда,
                    выпуск?.CommonName,
                    выпуск?.SerialNumber,
                    срок,
                    обновить,
                    заменён);
            })
            .OrderByDescending(з => з.Served)
            .ThenBy(з => з.Superseded)
            .ThenBy(з => з.Origin switch { "managed" => 0, "kmp" => 1, "foreign" => 2, "builtin" => 3, _ => 4 })
            .ThenBy(з => з.NotAfter ?? DateTimeOffset.MaxValue)];
    }

    /// <summary>Самое важное о сервере одним словом и фразой.</summary>
    internal static (string Code, string Text) Status(
        KerioServer с, SourceCertificate? выпуск, int warningDays, DateTimeOffset now, bool dryRun = false, bool paused = false,
        bool queued = false)
    {
        if (!с.Enabled)
        {
            return (ServerStatuses.Disabled, "Отключён: не проверяется и не обновляется.");
        }

        if (с.LastCheckAt is null)
        {
            return (ServerStatuses.Unchecked, "Ещё не проверялся — проверка в ближайшем проходе.");
        }

        if (с.ServedNotAfter is { } до && до <= now)
        {
            return (ServerStatuses.Expired, $"Сертификат истёк {до.ToUniversalTime():dd.MM.yyyy}.");
        }

        if (с.LastCheckError is { } ошибка)
        {
            return (ServerStatuses.Error, ошибка);
        }

        if (выпуск is not null && !Fingerprints.SameSerial(с.ServedSerial, выпуск.SerialNumber))
        {
            if (queued)
            {
                return (ServerStatuses.UpdatePending, "В Key Manager Plus новый выпуск — в очереди на установку.");
            }

            if (с.LastDeployError is { } отказ && Fingerprints.SameSerial(с.FailedSerial, выпуск.SerialNumber))
            {
                return (ServerStatuses.DeployFailed, отказ);
            }

            if (ПоУмолчаниюДольше(с, выпуск))
            {
                return (ServerStatuses.Ok, $"Действует сертификат не из KMP до {с.ServedNotAfter?.ToUniversalTime():dd.MM.yyyy} — дольше выпуска KMP; замена не нужна.");
            }

            // Как в DeployPlanner: сервис этот выпуск уже ставил, а сервер подаёт другой — не спорит, ждёт решения человека.
            if (Fingerprints.SameSerial(с.InstalledSerial, выпуск.SerialNumber))
            {
                return (ServerStatuses.UpdatePending, "Сертификат на сервере сменили мимо сервиса. Если это не намеренно, нажмите «Обновить».");
            }

            return (ServerStatuses.UpdatePending, dryRun
                ? "В Key Manager Plus новый выпуск — пробный режим: проверен, но в Kerio не ставится."
                : InstallPolicy.Automatic(!paused, с.AutoDeploy, с.InstalledSerial)
                    ? "В Key Manager Plus новый выпуск — в очереди на установку."
                    : "В Key Manager Plus новый выпуск — ждёт ручной установки.");
        }

        if (SyncCycle.Устаревший(с) is not null)
        {
            return (ServerStatuses.NameStale, "Свежий выпуск подаётся, но для этого имени Kerio подаёт прежний — перезапустите службу Kerio Connect.");
        }

        if (с.ServedNotAfter is { } срок && срок - now <= TimeSpan.FromDays(warningDays))
        {
            return (ServerStatuses.Expiring, $"Сертификат истекает {срок.ToUniversalTime():dd.MM.yyyy}, нового выпуска пока нет.");
        }

        if (string.IsNullOrWhiteSpace(с.CommonName))
        {
            return (ServerStatuses.Unbound, с.Extras.Count > 0
                ? $"Сертификат по умолчанию сервис не меняет; сертификатов по именам: {с.Extras.Count}."
                : "Сертификаты Key Manager Plus не выбраны — только наблюдение.");
        }

        if (выпуск is null)
        {
            return (ServerStatuses.Error, $"В Key Manager Plus нет сертификата {с.CommonName}.");
        }

        return (ServerStatuses.Ok, "Сертификат актуален.");
    }

    public static DeploymentOut Of(KerioDeployment у, bool steps) => new(
        у.Id,
        у.ServerId,
        у.ServerName,
        у.Trigger,
        у.Outcome,
        у.StartedAt,
        у.FinishedAt,
        у.ActorId,
        у.CommonName,
        у.SerialNumber,
        у.Sha256,
        у.NotAfter,
        у.PreviousSha256,
        у.KerioCertificateId,
        у.PreviousKerioCertificateId,
        у.FailedStep,
        у.Error,
        steps ? [.. у.Steps.Select(ш => new StepOut(ш.At, ш.Step, ш.Message, ш.Ok))] : null);
}
