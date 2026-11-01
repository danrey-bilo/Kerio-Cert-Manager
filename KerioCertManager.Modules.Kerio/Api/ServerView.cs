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
    /// <summary>Текущие выпуски KMP по Common Name (без учёта регистра).</summary>
    public static IReadOnlyDictionary<string, SourceCertificate> Current(IEnumerable<SourceCertificate> list) =>
        list.GroupBy(с => с.CommonName, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                г => г.Key,
                г => г.OrderByDescending(с => с.FirstSeenAt)
                    .ThenByDescending(с => с.ExpiresOn ?? DateOnly.MinValue)
                    .ThenByDescending(с => с.SerialNumber, StringComparer.Ordinal)
                    .First(),
                StringComparer.OrdinalIgnoreCase);

    public static ServerOut Of(
        KerioServer с,
        IReadOnlyDictionary<string, SourceCertificate> kmp,
        int warningDays,
        DateTimeOffset now,
        bool dryRun = false,
        IReadOnlyDictionary<string, SourceCertificate>? bySerial = null)
    {
        ArgumentNullException.ThrowIfNull(с);
        ArgumentNullException.ThrowIfNull(kmp);

        var выпуск = с.CommonName is { } имя && kmp.TryGetValue(имя, out var найден) ? найден : null;
        var (статус, текст) = Status(с, выпуск, warningDays, now, dryRun);

        int? дней = с.ServedNotAfter is { } до ? (int)Math.Floor((до - now).TotalDays) : null;

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
            с.DeployRequestedAt,
            с.DeployRequestedName,
            с.CreatedAt,
            с.UpdatedAt,
            bySerial is null ? [] : Хранилище(с, bySerial, warningDays, now),
            с.StoreCheckedAt,
            [.. с.Extras.Select(д => Доп(с, д, kmp, warningDays, now, dryRun))],
            с.ServedByName);
    }

    /// <summary>Сертификат своего имени: что в KMP, лежит ли в хранилище, подаётся ли по имени.</summary>
    internal static ExtraOut Доп(
        KerioServer с, ExtraCertificate д, IReadOnlyDictionary<string, SourceCertificate> kmp, int warningDays, DateTimeOffset now, bool dryRun)
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
            : д.LastError is { } отказ && Fingerprints.SameSerial(д.FailedSerial, выпуск.SerialNumber) ? (ServerStatuses.DeployFailed, отказ)
            : (ServerStatuses.UpdatePending, dryRun
                ? "Новый выпуск — пробный режим: проверен, но в Kerio не ставится."
                : с.AutoDeploy ? "Новый выпуск — установка в ближайшем проходе." : "Новый выпуск — ждёт ручной установки.");

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
        Хранилище(с, BySerial(releases), warningDays, now);

    /// <summary>Выпуски KMP по серийному номеру в одном виде — один раз на запрос, а не на каждый сервер.</summary>
    public static IReadOnlyDictionary<string, SourceCertificate> BySerial(IReadOnlyList<SourceCertificate> releases) =>
        releases
            .Select(в => (Номер: Fingerprints.Serial(в.SerialNumber), Выпуск: в))
            .Where(п => п.Номер.Length > 0)
            .GroupBy(п => п.Номер, StringComparer.Ordinal)
            .ToDictionary(г => г.Key, г => г.First().Выпуск, StringComparer.Ordinal);

    internal static IReadOnlyList<StoreCertOut> Хранилище(
        KerioServer с, IReadOnlyDictionary<string, SourceCertificate> поНомеру, int warningDays, DateTimeOffset now)
    {
        var ведомые = с.ManagedNames().ToHashSet(StringComparer.OrdinalIgnoreCase);

        return [.. с.Store
            .Select(з =>
            {
                var выпуск = з.Serial is { } номер && поНомеру.TryGetValue(Fingerprints.Serial(номер), out var найден) ? найден : null;
                var свой = (з.Sha256 is not null && string.Equals(з.Sha256, с.InstalledSha256, StringComparison.Ordinal))
                    || (выпуск is not null && ведомые.Contains(выпуск.CommonName))
                    || (с.CommonName is { } имя && ExtraPlanner.Того(з, имя) && с.Installed(з.Serial))
                    || с.Extras.Any(д => ExtraPlanner.Того(з, д.CommonName) && д.Installed(з.Serial));

                var откуда = свой ? "managed"
                    : выпуск is not null ? "kmp"
                    : з.BuiltIn ? "builtin"
                    : з.Type.Contains("Request", StringComparison.OrdinalIgnoreCase) ? "request"
                    : "foreign";

                int? дней = з.NotAfter is { } до ? (int)Math.Floor((до - now).TotalDays) : null;

                var срок = з.NotAfter is not { } конец ? "unknown"
                    : конец <= now ? "expired"
                    : конец - now <= TimeSpan.FromDays(warningDays) ? "expiring"
                    : "ok";

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
                    срок);
            })
            .OrderByDescending(з => з.Served)
            .ThenBy(з => з.Origin switch { "managed" => 0, "kmp" => 1, "foreign" => 2, "builtin" => 3, _ => 4 })
            .ThenBy(з => з.NotAfter ?? DateTimeOffset.MaxValue)];
    }

    /// <summary>Самое важное о сервере одним словом и фразой.</summary>
    internal static (string Code, string Text) Status(
        KerioServer с, SourceCertificate? выпуск, int warningDays, DateTimeOffset now, bool dryRun = false)
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

        var отстаёт = выпуск is not null && !Fingerprints.SameSerial(с.ServedSerial, выпуск.SerialNumber);

        if (с.LastDeployError is { } отказ && отстаёт)
        {
            return (ServerStatuses.DeployFailed, отказ);
        }

        if (отстаёт)
        {
            return (ServerStatuses.UpdatePending, dryRun
                ? "В Key Manager Plus новый выпуск — пробный режим: проверен, но в Kerio не ставится."
                : с.AutoDeploy
                    ? "В Key Manager Plus новый выпуск — установка в ближайшем проходе."
                    : "В Key Manager Plus новый выпуск — ждёт ручной установки.");
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
