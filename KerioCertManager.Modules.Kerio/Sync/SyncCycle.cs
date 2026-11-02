using System.Globalization;
using KerioCertManager.Modules.Kerio.Certificates;
using KerioCertManager.Modules.Kerio.Client;
using KerioCertManager.Modules.Kerio.Contracts;
using KerioCertManager.Modules.Kerio.Domain;
using KerioCertManager.Modules.Kerio.Notifications;
using KerioCertManager.Modules.Kerio.Settings;
using KerioCertManager.Modules.KeyManager.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Modules.Core.Contracts.Audit;
using Modules.Core.Contracts.Settings;
using Modules.Core.Data;
using Modules.Core.Errors;

namespace KerioCertManager.Modules.Kerio.Sync;

/// <summary>Аренда сервера: один проход за раз, сколько бы ни было процессов и нажатий.</summary>
internal sealed class ServerLeases(AppDbContext db, TimeProvider clock)
{
    /// <summary>Пока идёт проход, срок продлевает <see cref="LeaseHeartbeat"/>; умерший процесс отпускает сервер сам.</summary>
    internal static readonly TimeSpan Срок = TimeSpan.FromMinutes(5);

    public async Task<bool> TryAcquireAsync(Guid serverId, Guid owner, CancellationToken cancellationToken = default)
    {
        var сейчас = clock.GetUtcNow();
        var до = сейчас + Срок;

        return await db.Set<KerioServer>()
            .Where(с => с.Id == serverId && (с.LeaseUntil == null || с.LeaseUntil < сейчас))
            .ExecuteUpdateAsync(set => set.SetProperty(с => с.LeaseUntil, до).SetProperty(с => с.LeaseOwner, owner), cancellationToken) == 1;
    }

    /// <summary>Продлить аренду: проход ещё идёт.</summary>
    public Task ExtendAsync(Guid serverId, Guid owner)
    {
        var до = clock.GetUtcNow() + Срок;

        return db.Set<KerioServer>()
            .Where(с => с.Id == serverId && с.LeaseOwner == owner)
            .ExecuteUpdateAsync(set => set.SetProperty(с => с.LeaseUntil, до), CancellationToken.None);
    }

    public Task ReleaseAsync(Guid serverId, Guid owner) =>
        db.Set<KerioServer>()
            .Where(с => с.Id == serverId && с.LeaseOwner == owner)
            .ExecuteUpdateAsync(
                set => set.SetProperty(с => с.LeaseUntil, (DateTimeOffset?)null).SetProperty(с => с.LeaseOwner, (Guid?)null),
                CancellationToken.None);
}

/// <summary>Итог обработки сервера.</summary>
internal enum ProcessOutcome
{
    /// <summary>Обработан.</summary>
    Done,

    /// <summary>Сервер занят другим проходом.</summary>
    Busy,

    /// <summary>Сервера нет.</summary>
    Missing,
}

/// <summary>
/// Проход синхронизации: опрос Key Manager Plus, затем каждый сервер — проверка, очередь установки, письма.
/// За проход сервер ставит не больше одного сертификата, а во всём сервисе установка идёт одна за раз.
/// </summary>
internal sealed class SyncCycle(
    AppDbContext db,
    ICertificateSource source,
    ServerInspection inspection,
    KerioDeployer deployer,
    KerioNotifier notifier,
    ServerLeases leases,
    InstallGate gate,
    ISettingsReader settings,
    IAuditWriter audit,
    TimeProvider clock,
    IServiceScopeFactory scopes,
    ILogger<SyncCycle> log)
{
    /// <summary>Текст записи об установке, которую оборвала остановка сервиса.</summary>
    internal const string Прервана = "Установка прервана остановкой сервиса.";

    /// <summary>Сколько серверов проход проверяет одновременно: зависший Kerio не задерживает остальные.</summary>
    internal const int Параллельно = 4;

    /// <summary>⚠ Kerio 10.0.6 через ~25 с после импорта или удаления перезапускает HTTPS и обрывает сессии.</summary>
    internal static TimeSpan ПаузаПослеИзменения { get; set; } = TimeSpan.FromSeconds(40);

    private LeaseHeartbeat? _пульс;

    /// <summary>После <see cref="ProcessAsync"/>: у сервера остались сертификаты в очереди, и ставить их можно.</summary>
    internal bool ОчередьОсталась { get; private set; }

    /// <summary>Полный проход; <c>true</c> — очередь установки не пуста, следующий проход нужен скоро.</summary>
    public Task<bool> RunAsync(CancellationToken cancellationToken = default) => RunAsync(full: true, cancellationToken);

    /// <summary>
    /// Проход. Полный — опрос Key Manager Plus и все серверы; иначе проход очереди: только серверы, которым есть что
    /// ставить, по последнему опросу — остальных незачем дёргать каждые 45 секунд.
    /// </summary>
    public async Task<bool> RunAsync(bool full, CancellationToken cancellationToken)
    {
        await ЗакрытьБрошенныеAsync(cancellationToken);

        if (full)
        {
            var опрос = await source.RefreshAsync(cancellationToken);

            if (опрос is { Configured: true, Succeeded: false })
            {
                log.LogWarning("kerio_sync_source_failed {Error}", опрос.Error);
            }
        }

        var включённые = await db.Set<KerioServer>()
            .AsNoTracking()
            .Where(с => с.Enabled)
            .OrderBy(с => с.Name)
            .Select(с => new { с.Id, с.InstallQueue })
            .ToListAsync(cancellationToken);

        var нажатые = full
            ? []
            : await db.Set<InstallRequest>().AsNoTracking().Select(з => з.ServerId).Distinct().ToListAsync(cancellationToken);

        var серверы = включённые.Where(с => full || с.InstallQueue.Count > 0 || нажатые.Contains(с.Id)).Select(с => с.Id).ToList();
        var ещё = 0;

        // ⚠ Каждый сервер — в своей области: у прохода свой DbContext, а он не переносит параллельной работы.
        await Parallel.ForEachAsync(
            серверы,
            new ParallelOptions { MaxDegreeOfParallelism = Параллельно, CancellationToken = cancellationToken },
            async (id, token) =>
            {
                try
                {
                    await using var область = scopes.CreateAsyncScope();
                    var цикл = область.ServiceProvider.GetRequiredService<SyncCycle>();

                    // Занят другим проходом — очередь могла остаться: заглянуть снова скоро, а не через интервал.
                    if (await цикл.ProcessAsync(id, full, token) == ProcessOutcome.Busy || цикл.ОчередьОсталась)
                    {
                        Interlocked.Increment(ref ещё);
                    }
                }
                catch (Exception ошибка) when (!(ошибка is OperationCanceledException && token.IsCancellationRequested))
                {
                    log.LogError("kerio_sync_server_failed {Server} {Error}", id, SafeError.Describe(ошибка));
                }
            });

        return ещё > 0;
    }

    /// <summary>Установки «идёт», чей сервер никто не держит, — процесс умер посреди них: закрыть отказом.</summary>
    internal async Task<int> ЗакрытьБрошенныеAsync(CancellationToken cancellationToken)
    {
        var сейчас = clock.GetUtcNow();

        var закрыто = await db.Set<KerioDeployment>()
            .Where(у => у.Outcome == DeployOutcomes.Running
                && !db.Set<KerioServer>().Any(с => с.Id == у.ServerId && с.LeaseUntil != null && с.LeaseUntil > сейчас))
            .ExecuteUpdateAsync(
                set => set
                    .SetProperty(у => у.Outcome, DeployOutcomes.Failed)
                    .SetProperty(у => у.FinishedAt, сейчас)
                    .SetProperty(у => у.Error, Прервана),
                cancellationToken);

        if (закрыто > 0)
        {
            log.LogWarning("kerio_deploy_abandoned_closed {Count}", закрыто);
        }

        return закрыто;
    }

    /// <summary>Проверить сервер и, если пора, поставить сертификат; <paramref name="full"/> — считать сбои связи.</summary>
    public async Task<ProcessOutcome> ProcessAsync(Guid serverId, bool full = true, CancellationToken cancellationToken = default)
    {
        var владелец = Guid.CreateVersion7();

        if (!await leases.TryAcquireAsync(serverId, владелец, cancellationToken))
        {
            return await db.Set<KerioServer>().AnyAsync(с => с.Id == serverId, cancellationToken)
                ? ProcessOutcome.Busy
                : ProcessOutcome.Missing;
        }

        await using var пульс = new LeaseHeartbeat(scopes, clock, log, serverId, владелец);

        try
        {
            db.ChangeTracker.Clear();
            _пульс = пульс;
            ОчередьОсталась = false;

            var сервер = await db.Set<KerioServer>().FirstOrDefaultAsync(с => с.Id == serverId, cancellationToken);

            if (сервер is null)
            {
                return ProcessOutcome.Missing;
            }

            var запросы = await db.Set<InstallRequest>()
                .AsNoTracking()
                .Where(з => з.ServerId == serverId)
                .OrderBy(з => з.RequestedAt)
                .ToListAsync(cancellationToken);

            foreach (var запрос in запросы)
            {
                сервер.Requested.TryAdd(запрос.CommonName, запрос.RequestedBy);
            }

            await ОбработатьAsync(сервер, запросы, full, cancellationToken);

            return ProcessOutcome.Done;
        }
        finally
        {
            _пульс = null;
            await leases.ReleaseAsync(serverId, владелец);
        }
    }

    /// <summary>
    /// Только проверка — TLS и вход в API, без решений и установки. <paramref name="forceApi"/> — войти, даже если Kerio
    /// прежде отверг пароль: только по кнопке «Проверить».
    /// </summary>
    public async Task<ProcessOutcome> CheckAsync(Guid serverId, bool forceApi = true, CancellationToken cancellationToken = default)
    {
        var владелец = Guid.CreateVersion7();

        if (!await leases.TryAcquireAsync(serverId, владелец, cancellationToken))
        {
            return await db.Set<KerioServer>().AnyAsync(с => с.Id == serverId, cancellationToken)
                ? ProcessOutcome.Busy
                : ProcessOutcome.Missing;
        }

        try
        {
            db.ChangeTracker.Clear();

            var сервер = await db.Set<KerioServer>().FirstOrDefaultAsync(с => с.Id == serverId, cancellationToken);

            if (сервер is null)
            {
                return ProcessOutcome.Missing;
            }

            ServerInspection.Apply(сервер, await inspection.InspectAsync(сервер, forceApi, cancellationToken), clock.GetUtcNow());
            await db.SaveChangesAsync(cancellationToken);

            return ProcessOutcome.Done;
        }
        finally
        {
            await leases.ReleaseAsync(serverId, владелец);
            db.ChangeTracker.Clear();
        }
    }

    private async Task ОбработатьAsync(KerioServer сервер, IReadOnlyList<InstallRequest> запросы, bool full, CancellationToken cancellationToken)
    {
        // ⚠ Нажатие не заставляет входить с отвергнутым паролем: это приближало бы блокировку учётной записи.
        var проверка = await inspection.InspectAsync(сервер, forceApi: false, cancellationToken);

        ServerInspection.Apply(сервер, проверка, clock.GetUtcNow(), считатьСбои: full);
        await db.SaveChangesAsync(cancellationToken);

        var предупреждения = new List<ServerAlert>(
            Связь(сервер, проверка, await settings.GetAsync(KerioSettings.AlertUnreachableChecks, cancellationToken)));

        var авто = await settings.GetAsync(KerioSettings.AutoDeploy, cancellationToken);
        var дней = await settings.GetAsync(KerioSettings.WarningDays, cancellationToken);
        var пробно = await settings.GetAsync(KerioSettings.DryRun, cancellationToken);
        var часов = await settings.GetAsync(KerioSettings.AlertOverdueHours, cancellationToken);
        var выпуски = SourceReleases.Current(await source.ListAsync(cancellationToken));

        // Интеграция выключена — из неё ничего не ставится, в том числе по старому снимку.
        var нельзя = !await source.AvailableAsync(cancellationToken)
            ? "Key Manager Plus выключен в настройках."
            : проверка.Error;

        var текущий = сервер.CommonName is { } выбран ? выпуски.GetValueOrDefault(выбран) : null;
        var план = DeployPlanner.Plan(сервер, проверка.Served, текущий, авто, дней, clock.GetUtcNow());

        if (план.Action == PlanAction.Adopt && проверка.Served is { } подан && план.Source is { } свой
            && await ВыпускKmpAsync(сервер, подан.Sha256, подан.Serial, свой, null, предупреждения, cancellationToken))
        {
            // ⚠ Сервер подаёт ровно сертификат из KMP (сверено по SHA-256) и прошёл рукопожатие — значит, владеет его ключом.
            if (сервер.TlsMode == TlsModes.Pin
                && сервер.InstalledSha256 is not null
                && string.Equals(сервер.TlsPinSha256, сервер.InstalledSha256, StringComparison.Ordinal))
            {
                audit.Write(new AuditEntry(
                    KerioActions.TrustUpdated,
                    ActorType: AuditActorType.System,
                    ResourceType: KerioActions.ServerResource,
                    ResourceId: сервер.Id,
                    Reason: "adopted",
                    Changes: new Dictionary<string, object?>(StringComparer.Ordinal)
                    {
                        ["server"] = сервер.Name,
                        ["tls_pin_sha256"] = new[] { сервер.TlsPinSha256, подан.Sha256 },
                    }));

                сервер.TlsPinSha256 = подан.Sha256;
            }

            предупреждения.RemoveAll(а => а.Kind == AlertKinds.NotTrusted);

            сервер.PreviousSerials = InstalledHistory.Push(сервер.InstalledSerial, сервер.PreviousSerials, свой.SerialNumber);
            сервер.InstalledSerial = свой.SerialNumber;
            сервер.InstalledSha256 = подан.Sha256;
            сервер.InstalledNotBefore = подан.NotBefore;
            сервер.InstalledNotAfter = подан.NotAfter;
            сервер.InstalledAt ??= clock.GetUtcNow();
            сервер.InstalledKerioId = сервер.Store.FirstOrDefault(з => string.Equals(з.Sha256, подан.Sha256, StringComparison.Ordinal))?.Id;
            сервер.FailedSerial = null;
            сервер.DeployFailures = 0;
            сервер.NextAttemptAt = null;
            сервер.LastDeployError = null;

            log.LogInformation("kerio_certificate_adopted {Server} {CommonName} {Serial}", сервер.Name, свой.CommonName, свой.SerialNumber);
        }

        var кандидаты = new List<InstallCandidate>();
        var установки = new Dictionary<string, Func<CancellationToken, Task<DeployResult>>>(StringComparer.OrdinalIgnoreCase);

        DateTimeOffset? Нажат(string имя) =>
            запросы.FirstOrDefault(з => string.Equals(з.CommonName, имя, StringComparison.OrdinalIgnoreCase))?.RequestedAt;

        if (план.Action == PlanAction.Deploy && план.Source is { } выпуск && сервер.CommonName is { } имяПоУмолчанию
            && ВОчередь(сервер, имяПоУмолчанию, выпуск, сервер.DryRunSerial, нельзя, пробно, отказ =>
            {
                сервер.LastDeployError = отказ;
                сервер.FailedSerial = выпуск.SerialNumber;
            }))
        {
            кандидаты.Add(new InstallCandidate(имяПоУмолчанию, true, Нажат(имяПоУмолчанию), сервер.ServedNotAfter));
            установки[имяПоУмолчанию] = token => УстановитьAsync(сервер, план, выпуск, пробно, token);
        }

        var свои = await ДопAsync(сервер, проверка, выпуски, авто, дней, предупреждения, cancellationToken);

        foreach (var (доп, планДоп) in свои)
        {
            if (планДоп.Action == PlanAction.Deploy && планДоп.Source is { } выпускДоп
                && ВОчередь(сервер, доп.CommonName, выпускДоп, доп.DryRunSerial, нельзя, пробно, отказ =>
                {
                    доп.LastError = отказ;
                    доп.FailedSerial = выпускДоп.SerialNumber;
                }))
            {
                var лежит = сервер.Store.Where(з => ExtraPlanner.Того(з, доп.CommonName)).Max(з => з.NotAfter);

                кандидаты.Add(new InstallCandidate(доп.CommonName, false, Нажат(доп.CommonName), лежит));
                установки[доп.CommonName] = token =>
                    УстановитьДопAsync(сервер, доп, планДоп.Trigger ?? DeployTriggers.Renewal, выпускДоп, пробно, token);
            }
        }

        var очередь = InstallOrder.Sort(кандидаты, clock.GetUtcNow()).Select(к => к.CommonName).ToList();
        string? поставлен = null;
        DeployResult? итог = null;

        // ⚠ Один сертификат за проход: остальные ждут следующего, а сервер не занят установками на час.
        if (очередь.Count > 0 && нельзя is null)
        {
            // В очереди несколько — об удачах одно письмо, когда она опустеет; отказы пишутся сразу.
            if (очередь.Count > 1)
            {
                сервер.QueueReportSince ??= clock.GetUtcNow();
            }

            итог = await ПоочерёдноAsync(сервер, установки[очередь[0]], cancellationToken);

            if (итог is not null)
            {
                поставлен = очередь[0];
                очередь.RemoveAt(0);

                if (итог.Outcome is DeployOutcomes.Succeeded or DeployOutcomes.RolledBack
                    || (итог.Outcome == DeployOutcomes.Failed && итог.KerioId is not null))
                {
                    await ОбновитьХранилищеAsync(сервер, cancellationToken);
                }
            }
        }
        else if (очередь.Count > 0)
        {
            log.LogWarning("kerio_deploy_skipped {Server} {Reason}", сервер.Name, нельзя);
        }

        сервер.InstallQueue = очередь;
        ОчередьОсталась = очередь.Count > 0 && нельзя is null;

        // Встал — о его сроке писать незачем; ждёт своей очереди — скоро встанет: письмо лишнее, но счёт повторов идёт.
        IEnumerable<ServerAlert> ПоОчереди(IEnumerable<ServerAlert> условия, string? имя)
        {
            var встал = итог?.Outcome == DeployOutcomes.Succeeded && string.Equals(поставлен, имя, StringComparison.OrdinalIgnoreCase);
            var ждёт = ОчередьОсталась && очередь.Contains(имя, StringComparer.OrdinalIgnoreCase);

            return условия
                .Where(а => !встал || а.Kind is not (AlertKinds.Expired or AlertKinds.Expiring))
                .Select(а => ждёт && а.Kind is AlertKinds.Expired or AlertKinds.Expiring ? а with { Quiet = true } : а);
        }

        предупреждения.AddRange(ПоОчереди(план.Alerts, сервер.CommonName));

        foreach (var (доп, планДоп) in свои)
        {
            предупреждения.AddRange(ПоОчереди(планДоп.Alerts, доп.CommonName));

            if (ПросрочкаДоп(сервер, доп, планДоп, часов, дней, clock.GetUtcNow(), пробно) is { } просрочкаДоп)
            {
                предупреждения.Add(просрочкаДоп);
            }
        }

        if (Устаревший(сервер) is { } поИмени)
        {
            предупреждения.Add(поИмени);
        }

        // ⚠ Запрос гасится, когда имени нет в очереди: поставлен, не нужен или поставить нельзя. Новые нажатия не трогаем.
        var погасить = запросы
            .Where(з => !очередь.Contains(з.CommonName, StringComparer.OrdinalIgnoreCase))
            .Select(з => з.Id)
            .ToList();

        if (погасить.Count > 0)
        {
            await db.Set<InstallRequest>().Where(з => погасить.Contains(з.Id)).ExecuteDeleteAsync(cancellationToken);
        }

        if (Просрочка(сервер, план, часов, дней, clock.GetUtcNow(), пробно) is { } просрочка)
        {
            предупреждения.Add(просрочка);
        }

        await db.SaveChangesAsync(cancellationToken);

        if (очередь.Count == 0 && сервер.QueueReportSince is { } с)
        {
            await СводкаAsync(сервер, с, cancellationToken);
        }

        // ⚠ Связь или хранилище не прочитаны — условия не проверены, а не ушли: ключи повторов остаются.
        await notifier.AlertAsync(сервер, предупреждения, проверка.Served is not null && проверка.Store is not null, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
    }

    /// <summary>Очередь опустела: одно письмо обо всех установках с <paramref name="since"/>; не вышло — попробуем следующим проходом.</summary>
    private async Task СводкаAsync(KerioServer сервер, DateTimeOffset since, CancellationToken cancellationToken)
    {
        if (await settings.GetAsync(KerioSettings.AlertDeployed, cancellationToken))
        {
            var установки = await db.Set<KerioDeployment>()
                .AsNoTracking()
                .Where(у => у.ServerId == сервер.Id && у.StartedAt >= since)
                .OrderBy(у => у.StartedAt)
                .Select(у => new QueueReport.Entry(у.CommonName, у.Outcome, у.NotAfter, у.Error))
                .ToListAsync(cancellationToken);

            // ⚠ Ничего не встало — об отказах уже ушли письма с повтором по сроку: сводка «0 из N» только мешала бы.
            if (QueueReport.Build(сервер.Name, установки) is { Installed: > 0 } сводка
                && !await notifier.SendQueueReportAsync(сервер, сводка, cancellationToken))
            {
                return;
            }
        }

        сервер.QueueReportSince = null;
    }

    /// <summary>
    /// Встаёт ли сертификат в очередь. Ставить сейчас нельзя — нажатый отказывает с причиной, остальные ждут; пробный
    /// прогон того же выпуска не повторяется.
    /// </summary>
    private static bool ВОчередь(
        KerioServer сервер,
        string имя,
        SourceCertificate выпуск,
        string? пробныйВыпуск,
        string? нельзя,
        bool пробно,
        Action<string> отказ)
    {
        var ручной = сервер.ManualFor(имя);

        if (нельзя is not null && ручной)
        {
            отказ($"Установка не начата: {нельзя}");
            return false;
        }

        return !(пробно && !ручной && Fingerprints.SameSerial(пробныйВыпуск, выпуск.SerialNumber));
    }

    /// <summary>
    /// Установка через общий слот: пока ставит другой сервер, этот ждёт своей очереди. Не дождался — <c>null</c>:
    /// сертификат остаётся в очереди, а место в проходе достаётся проверкам остальных серверов.
    /// </summary>
    private async Task<DeployResult?> ПоочерёдноAsync(
        KerioServer сервер, Func<CancellationToken, Task<DeployResult>> поставить, CancellationToken cancellationToken)
    {
        await ПаузаПослеУстановкиAsync(сервер, cancellationToken);

        var владелец = Guid.CreateVersion7();
        var до = clock.GetUtcNow() + InstallGate.Ожидание;

        while (!await gate.TryAcquireAsync(владелец, cancellationToken))
        {
            if (clock.GetUtcNow() >= до)
            {
                log.LogInformation("kerio_install_slot_busy {Server}", сервер.Name);
                return null;
            }

            await Task.Delay(InstallGate.Опрос, clock, cancellationToken);
        }

        if (_пульс is { } пульс)
        {
            пульс.Slot = владелец;
        }

        try
        {
            return await поставить(cancellationToken);
        }
        finally
        {
            if (_пульс is { } был)
            {
                был.Slot = null;
            }

            await gate.ReleaseAsync(владелец);
        }
    }

    /// <summary>Тот ли это сертификат, что выпуск KMP, — по SHA-256, а не по серийному номеру: короткие серийные совпадают у чужих.</summary>
    private async Task<bool> ВыпускKmpAsync(
        KerioServer сервер,
        string sha256,
        string? serial,
        SourceCertificate выпуск,
        string? scope,
        List<ServerAlert> предупреждения,
        CancellationToken cancellationToken)
    {
        try
        {
            var сертификаты = await source.FetchCertificatesAsync(выпуск.CommonName, выпуск.SerialNumber, cancellationToken);

            if (сертификаты.Any(der => Fingerprints.Sha256(der) == sha256))
            {
                return true;
            }
        }
        catch (CertificateSourceException ошибка)
        {
            log.LogWarning("kerio_adopt_deferred {Server} {Error}", сервер.Name, ошибка.Message);
            return false;
        }

        log.LogWarning("kerio_adopt_mismatch {Server} {Serial} {Sha256}", сервер.Name, serial, sha256);

        предупреждения.Add(new ServerAlert(
            AlertKinds.NotTrusted,
            AlertLevel.Critical,
            $"«{сервер.Name}» подаёт сертификат с номером выпуска KMP, но это другой сертификат",
            $"Серийный номер {serial} совпадает с выпуском {выпуск.CommonName} в Key Manager Plus, а отпечаток — нет. "
            + "Так выглядит подмена сервера или ручная установка чужого сертификата. Сервис его не признаёт и не доверяет ему.",
            scope));

        return false;
    }

    /// <summary>Предупреждения о связи: не отвечает, пароль отвергнут, нет прав, сертификат не доверен.</summary>
    internal static IEnumerable<ServerAlert> Связь(KerioServer сервер, InspectionResult проверка, int порог)
    {
        if (проверка.Served is null && сервер.TlsFailures >= порог)
        {
            yield return new ServerAlert(
                AlertKinds.Unreachable,
                AlertLevel.Critical,
                $"Kerio «{сервер.Name}» не отвечает",
                $"Сервис не может соединиться с {сервер.BaseUrl}: {проверка.TlsError} Проверок подряд без ответа: {сервер.TlsFailures}.");
        }

        switch (проверка.ApiFailure)
        {
            // Пока пароль отвергнут, сервис не входит — но напоминает по сроку повтора, пока не исправят.
            case KerioFailure.Authentication when проверка.ApiChecked || сервер.AuthRejected:
                yield return new ServerAlert(
                    AlertKinds.AuthRejected,
                    AlertLevel.Critical,
                    $"Kerio «{сервер.Name}» не принял учётную запись сервиса",
                    $"{проверка.ApiError} Задайте пароль учётной записи {сервер.Username} заново на странице сервера. До этого "
                    + "сервис не входит в Kerio, чтобы не заблокировать учётную запись.");
                break;

            case KerioFailure.Forbidden:
                yield return new ServerAlert(
                    AlertKinds.NotAdmin, AlertLevel.Critical, $"У учётной записи сервиса нет прав на «{сервер.Name}»", проверка.ApiError ?? string.Empty);
                break;

            case KerioFailure.Tls:
                yield return new ServerAlert(
                    AlertKinds.NotTrusted,
                    AlertLevel.Critical,
                    $"Сертификат Kerio «{сервер.Name}» не прошёл проверку доверия",
                    $"{проверка.ApiError} Если сертификат сменили намеренно, подтвердите новый отпечаток на странице сервера.");
                break;

            case KerioFailure.Transport or KerioFailure.Protocol or KerioFailure.Rejected when сервер.CheckFailures >= порог:
                yield return new ServerAlert(
                    AlertKinds.Unreachable,
                    AlertLevel.Critical,
                    $"API Kerio «{сервер.Name}» недоступен",
                    $"{проверка.ApiError} Проверок подряд с ошибкой: {сервер.CheckFailures}.");
                break;
        }
    }

    /// <summary>Перечитать хранилище Kerio после установки — только его, без счёта проверок и решений.</summary>
    private async Task ОбновитьХранилищеAsync(KerioServer сервер, CancellationToken cancellationToken)
    {
        try
        {
            var снова = await inspection.InspectAsync(сервер, forceApi: false, cancellationToken);

            if (снова.Store is not null)
            {
                сервер.Store = [.. снова.Store];
                сервер.StoreCheckedAt = clock.GetUtcNow();
            }

            if (снова.ServedByName is not null)
            {
                сервер.ServedByName = new Dictionary<string, string>(снова.ServedByName, StringComparer.Ordinal);
            }
        }
        catch (Exception ошибка) when (ошибка is not OperationCanceledException)
        {
            log.LogWarning("kerio_store_refresh_failed {Server} {Error}", сервер.Name, SafeError.Describe(ошибка));
        }
    }

    /// <summary>Сертификаты своих имён: признать лежащие и решить по каждому — ставить ли.</summary>
    private async Task<IReadOnlyList<(ExtraCertificate Доп, ExtraPlan План)>> ДопAsync(
        KerioServer сервер,
        InspectionResult проверка,
        IReadOnlyDictionary<string, SourceCertificate> выпуски,
        bool авто,
        int дней,
        List<ServerAlert> предупреждения,
        CancellationToken cancellationToken)
    {
        var итог = new List<(ExtraCertificate, ExtraPlan)>();

        foreach (var доп in сервер.Extras)
        {
            if (string.Equals(доп.CommonName, сервер.CommonName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var план = ExtraPlanner.Plan(
                сервер, доп, выпуски.GetValueOrDefault(доп.CommonName), проверка.Store is not null, авто, дней, clock.GetUtcNow(), сервер.ManualFor(доп.CommonName));

            if (план.Action == PlanAction.Adopt && план.Present is { Sha256: { } отпечаток } лежит && план.Source is { } свой
                && await ВыпускKmpAsync(сервер, отпечаток, лежит.Serial, свой, доп.CommonName, предупреждения, cancellationToken))
            {
                доп.PreviousSerials = [.. InstalledHistory.Push(доп.InstalledSerial, доп.PreviousSerials, свой.SerialNumber)];
                доп.InstalledSerial = свой.SerialNumber;
                доп.InstalledSha256 = лежит.Sha256;
                доп.InstalledNotAfter = лежит.NotAfter;
                доп.InstalledAt ??= clock.GetUtcNow();
                доп.KerioId = лежит.Id;
                доп.FailedSerial = null;
                доп.Failures = 0;
                доп.NextAttemptAt = null;
                доп.LastError = null;

                log.LogInformation("kerio_extra_adopted {Server} {CommonName} {Serial}", сервер.Name, свой.CommonName, свой.SerialNumber);
            }

            итог.Add((доп, план));
        }

        return итог;
    }

    /// <summary>Почему выпуск всё ещё не стоит — для письма «не обновился сам».</summary>
    private static string ПочемуНеВстал(KerioServer сервер, string? отказ, bool ждётКнопки, bool пробно) =>
        пробно ? "Включён пробный режим: сертификаты в Kerio не меняются."
        : отказ is not null ? $"Последняя установка не удалась: {отказ}"
        : сервер.LastCheckError is { } связь ? $"Связь с Kerio: {связь}"
        : ждётКнопки ? "Автоматическая установка выключена — нужна кнопка «Обновить» у сертификата во вкладке «Обзор»."
        : "Установка пока не прошла.";

    /// <summary>«Не обновился сам» для сертификата своего имени: отсчёт с продления, но не раньше добавления в список.</summary>
    internal static ServerAlert? ПросрочкаДоп(
        KerioServer сервер, ExtraCertificate доп, ExtraPlan план, int часов, int warningDays, DateTimeOffset now, bool пробно = false)
    {
        if (!план.Behind || план.Source is not { } нужный || Fingerprints.SameSerial(доп.InstalledSerial, нужный.SerialNumber))
        {
            return null;
        }

        var с = нужный.FirstSeenAt > доп.AddedAt ? нужный.FirstSeenAt : доп.AddedAt;
        var ждёт = now - с;

        if (ждёт < TimeSpan.FromHours(часов))
        {
            return null;
        }

        var почему = ПочемуНеВстал(сервер, доп.LastError, план.Alerts.Any(а => а.Kind == AlertKinds.RenewalPending), пробно);
        var старый = сервер.Store.Where(з => ExtraPlanner.Того(з, доп.CommonName)).Max(з => з.NotAfter);
        var срочно = старый is { } до && до - now <= TimeSpan.FromDays(warningDays);

        return new ServerAlert(
            AlertKinds.Overdue,
            срочно ? AlertLevel.Critical : AlertLevel.Warning,
            $"«{сервер.Name}»: {доп.CommonName} не обновился сам — новый выпуск ждёт {((int)ждёт.TotalHours).ToString(CultureInfo.InvariantCulture)} ч",
            $"В Key Manager Plus выпуск {нужный.CommonName} (серийный {Fingerprints.Serial(нужный.SerialNumber)}) замечен "
            + $"{с.ToUniversalTime():dd.MM.yyyy HH:mm} UTC, а в хранилище Kerio его всё ещё нет. {почему}",
            доп.CommonName);
    }

    /// <summary>Для имени сертификата по умолчанию Kerio подаёт не поставленный сервисом — держит прежнюю версию.</summary>
    internal static ServerAlert? Устаревший(KerioServer сервер)
    {
        if (сервер.CommonName is not { } имяKmp
            || Sni.For(имяKmp) is not { } имя
            || сервер.InstalledSha256 is not { } свой
            || !string.Equals(сервер.ServedSha256, свой, StringComparison.Ordinal)
            || !сервер.ServedByName.TryGetValue(имя, out var поИмени)
            || string.Equals(поИмени, свой, StringComparison.Ordinal))
        {
            return null;
        }

        return new ServerAlert(
            AlertKinds.NameStale,
            AlertLevel.Critical,
            $"«{сервер.Name}» подаёт для {имя} не новый сертификат",
            $"По адресу администрирования сервер подаёт установленный {имяKmp}, а клиентам, которые обращаются по имени {имя}, — "
            + "другой. Kerio подхватывает новую версию после удаления прежней или перезапуска службы Kerio Connect.");
    }

    /// <summary>«Не обновился сам»: выпуск KMP должен стоять, а его нет дольше <c>kerio.alert_overdue_hours</c>.</summary>
    internal static ServerAlert? Просрочка(
        KerioServer сервер, DeployPlan план, int часов, int warningDays, DateTimeOffset now, bool пробно = false)
    {
        if (!план.Behind || план.Source is not { } нужный || Fingerprints.SameSerial(сервер.ServedSerial, нужный.SerialNumber))
        {
            return null;
        }

        var выбран = сервер.DefaultSelectedAt ?? сервер.CreatedAt;
        var с = нужный.FirstSeenAt > выбран ? нужный.FirstSeenAt : выбран;
        var ждёт = now - с;

        if (ждёт < TimeSpan.FromHours(часов))
        {
            return null;
        }

        var почему = ПочемуНеВстал(сервер, сервер.LastDeployError, план.Alerts.Any(а => а.Kind == AlertKinds.RenewalPending), пробно);
        var срочно = сервер.ServedNotAfter is { } до && до - now <= TimeSpan.FromDays(warningDays);
        var часовЖдёт = ((int)ждёт.TotalHours).ToString(CultureInfo.InvariantCulture);

        return new ServerAlert(
            AlertKinds.Overdue,
            срочно ? AlertLevel.Critical : AlertLevel.Warning,
            $"«{сервер.Name}» не обновился сам: новый сертификат ждёт {часовЖдёт} ч",
            $"В Key Manager Plus выпуск {нужный.CommonName} (серийный {Fingerprints.Serial(нужный.SerialNumber)}) замечен "
            + $"{с.ToUniversalTime():dd.MM.yyyy HH:mm} UTC, а сервер всё ещё подаёт прежний сертификат. {почему}");
    }

    /// <summary>Поставить сертификат по умолчанию — с проверкой на всех портах и откатом.</summary>
    private Task<DeployResult> УстановитьAsync(
        KerioServer сервер, DeployPlan план, SourceCertificate выпуск, bool пробно, CancellationToken cancellationToken)
    {
        var ручной = план.Trigger == DeployTriggers.Manual;

        return ЗапуститьAsync(
            сервер,
            план.Trigger ?? DeployTriggers.Renewal,
            выпуск,
            пробно,
            async (журнал, token) => await deployer.DeployAsync(
                new DeployRequest(
                    сервер,
                    выпуск,
                    ручной,
                    пробно,
                    TimeSpan.FromSeconds(await settings.GetAsync(KerioSettings.VerifySeconds, token)),
                    RequireRollbackTarget: !ручной && сервер.InstalledSerial is not null),
                журнал,
                token),
            (итог, сейчас) => ПрименитьОсновной(сервер, выпуск, итог, сейчас),
            () => сервер.NextAttemptAt,
            дополнительный: false,
            cancellationToken);
    }

    /// <summary>Положить в хранилище сертификат своего имени, не меняя сертификат по умолчанию.</summary>
    private Task<DeployResult> УстановитьДопAsync(
        KerioServer сервер, ExtraCertificate доп, string trigger, SourceCertificate выпуск, bool пробно, CancellationToken cancellationToken) =>
        ЗапуститьAsync(
            сервер,
            trigger,
            выпуск,
            пробно,
            async (журнал, token) => await deployer.DeployDomainAsync(
                new DeployRequest(
                    сервер,
                    выпуск,
                    trigger == DeployTriggers.Manual,
                    пробно,
                    TimeSpan.FromSeconds(await settings.GetAsync(KerioSettings.VerifySeconds, token))),
                журнал,
                token),
            (итог, сейчас) => ПрименитьДоп(сервер, доп, выпуск, итог, сейчас),
            () => доп.NextAttemptAt,
            дополнительный: true,
            cancellationToken);

    /// <summary>Хранилище недавно менялось — дождаться перезапуска HTTPS, не занимая общий слот.</summary>
    private async Task ПаузаПослеУстановкиAsync(KerioServer сервер, CancellationToken cancellationToken)
    {
        if (сервер.LastDeployAt is { } когда && clock.GetUtcNow() - когда < ПаузаПослеИзменения)
        {
            var ждать = ПаузаПослеИзменения - (clock.GetUtcNow() - когда);
            log.LogInformation("kerio_deploy_waits_restart {Server} {Seconds}", сервер.Name, (int)ждать.TotalSeconds);
            await Task.Delay(ждать, clock, cancellationToken);
        }
    }

    /// <summary>Установка с журналом: запись «идёт», шаги по мере выполнения, итог, журнал действий, письмо.</summary>
    private async Task<DeployResult> ЗапуститьAsync(
        KerioServer сервер,
        string trigger,
        SourceCertificate выпуск,
        bool пробно,
        Func<Func<DeployStep, Task>, CancellationToken, Task<DeployResult>> поставить,
        Action<DeployResult, DateTimeOffset> применить,
        Func<DateTimeOffset?> следующая,
        bool дополнительный,
        CancellationToken cancellationToken)
    {
        var ручной = trigger == DeployTriggers.Manual;

        var установка = new KerioDeployment
        {
            Id = Guid.CreateVersion7(),
            ServerId = сервер.Id,
            ServerName = сервер.Name,
            Trigger = trigger,
            Outcome = DeployOutcomes.Running,
            StartedAt = clock.GetUtcNow(),
            ActorId = ручной && сервер.Requested.TryGetValue(выпуск.CommonName, out var кто) ? кто : null,
            CommonName = выпуск.CommonName,
            SerialNumber = выпуск.SerialNumber,
            PreviousSha256 = дополнительный ? null : сервер.ServedSha256,
        };

        db.Set<KerioDeployment>().Add(установка);
        await db.SaveChangesAsync(cancellationToken);

        log.LogInformation(
            "kerio_deploy_started {Server} {CommonName} {Serial} {Trigger} {DryRun} {Extra}",
            сервер.Name, выпуск.CommonName, выпуск.SerialNumber, установка.Trigger, пробно, дополнительный);

        DeployResult итог;

        try
        {
            итог = await поставить(
                async шаг =>
                {
                    установка.Steps.Add(шаг);
                    await db.SaveChangesAsync(CancellationToken.None);
                },
                cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            установка.Outcome = DeployOutcomes.Failed;
            установка.FinishedAt = clock.GetUtcNow();
            установка.Error = Прервана;
            await db.SaveChangesAsync(CancellationToken.None);
            throw;
        }

        var сейчас = clock.GetUtcNow();

        установка.Outcome = итог.Outcome;
        установка.FinishedAt = сейчас;
        установка.Sha256 = итог.Sha256;
        установка.NotAfter = итог.NotAfter;
        установка.KerioCertificateId = итог.KerioId;
        установка.PreviousKerioCertificateId = итог.PreviousKerioId;
        установка.PreviousSha256 = дополнительный ? null : установка.PreviousSha256 ?? итог.PreviousSha256;
        установка.FailedStep = итог.FailedStep;
        установка.Error = итог.Error;

        var пинБыл = сервер.TlsPinSha256;

        // ⚠ Пробный прогон Kerio не менял: ни времени установки, ни паузы на перезапуск HTTPS.
        if (итог.Outcome != DeployOutcomes.DryRun)
        {
            сервер.LastDeployAt = сейчас;
        }

        применить(итог, сейчас);

        // ⚠ Итог установки и строка журнала.
        audit.Write(new AuditEntry(
            итог.Outcome switch
            {
                DeployOutcomes.Succeeded => KerioActions.CertificateDeployed,
                DeployOutcomes.DryRun => KerioActions.DeployDryRun,
                DeployOutcomes.RolledBack => KerioActions.DeployRolledBack,
                _ => KerioActions.DeployFailed,
            },
            ActorId: установка.ActorId,
            ActorType: установка.ActorId is null ? AuditActorType.System : AuditActorType.User,
            ResourceType: KerioActions.ServerResource,
            ResourceId: сервер.Id,
            Reason: итог.Error is null ? установка.Trigger : $"{итог.FailedStep}: {итог.Error}",
            Changes: new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["server"] = сервер.Name,
                ["common_name"] = выпуск.CommonName,
                ["serial_number"] = выпуск.SerialNumber,
                ["outcome"] = итог.Outcome,
                ["trigger"] = установка.Trigger,
                ["sha256"] = итог.Sha256,
                ["deployment"] = установка.Id,
                ["role"] = дополнительный ? "name" : "default",
                ["tls_pin_moved"] = !string.Equals(пинБыл, сервер.TlsPinSha256, StringComparison.Ordinal),
            }));

        await db.SaveChangesAsync(CancellationToken.None);

        log.LogInformation(
            "kerio_deploy_finished {Server} {CommonName} {Serial} {Outcome} {Step}",
            сервер.Name, выпуск.CommonName, выпуск.SerialNumber, итог.Outcome, итог.FailedStep);

        await ОтчётAsync(сервер, установка, итог, следующая(), дополнительный, cancellationToken);

        return итог;
    }

    /// <summary>Итог установки сертификата по умолчанию — в строку сервера.</summary>
    private static void ПрименитьОсновной(KerioServer сервер, SourceCertificate выпуск, DeployResult итог, DateTimeOffset сейчас)
    {
        switch (итог.Outcome)
        {
            case DeployOutcomes.Succeeded:
                // ⚠ Закреплённый отпечаток был у прежнего сертификата — теперь порт подаёт новый, поставленный нами.
                if (сервер.TlsMode == TlsModes.Pin
                    && итог.Sha256 is not null
                    && string.Equals(сервер.TlsPinSha256, итог.PreviousSha256, StringComparison.Ordinal))
                {
                    сервер.TlsPinSha256 = итог.Sha256;
                }

                сервер.PreviousSerials = InstalledHistory.Push(сервер.InstalledSerial, сервер.PreviousSerials, выпуск.SerialNumber);
                сервер.InstalledSerial = выпуск.SerialNumber;
                сервер.InstalledSha256 = итог.Sha256;
                сервер.InstalledNotBefore = итог.NotBefore;
                сервер.InstalledNotAfter = итог.NotAfter;
                сервер.InstalledAt = сейчас;
                сервер.InstalledKerioId = итог.KerioId;
                сервер.ServedSha256 = итог.Sha256;
                сервер.ServedSerial = Fingerprints.Serial(выпуск.SerialNumber);
                сервер.ServedNotBefore = итог.NotBefore;
                сервер.ServedNotAfter = итог.NotAfter;
                сервер.ServedSubject = выпуск.CommonName;
                сервер.LastDeployError = null;
                сервер.FailedSerial = null;
                сервер.DeployFailures = 0;
                сервер.NextAttemptAt = null;
                сервер.DryRunSerial = null;

                // ⚠ Проверка TLS после перезапуска могла не успеть: прежнее «что подаётся по имени» больше не верно.
                if (Sni.For(выпуск.CommonName) is { } имя)
                {
                    сервер.ServedByName.Remove(имя);
                }

                break;

            case DeployOutcomes.DryRun:
                сервер.DryRunSerial = выпуск.SerialNumber;
                сервер.LastDeployError = null;
                break;

            default:
                сервер.DeployFailures = Fingerprints.SameSerial(сервер.FailedSerial, выпуск.SerialNumber) ? сервер.DeployFailures + 1 : 1;
                сервер.FailedSerial = выпуск.SerialNumber;
                сервер.NextAttemptAt = сейчас + DeployPlanner.Backoff(сервер.DeployFailures);
                сервер.LastDeployError = итог.Error;
                break;
        }
    }

    /// <summary>Итог установки сертификата своего имени — в его запись в списке сервера.</summary>
    private static void ПрименитьДоп(KerioServer сервер, ExtraCertificate доп, SourceCertificate выпуск, DeployResult итог, DateTimeOffset сейчас)
    {
        доп.LastDeployAt = сейчас;

        switch (итог.Outcome)
        {
            case DeployOutcomes.Succeeded:
                доп.PreviousSerials = [.. InstalledHistory.Push(доп.InstalledSerial, доп.PreviousSerials, выпуск.SerialNumber)];
                доп.InstalledSerial = выпуск.SerialNumber;
                доп.InstalledSha256 = итог.Sha256;
                доп.InstalledNotAfter = итог.NotAfter;
                доп.InstalledAt = сейчас;
                доп.KerioId = итог.KerioId;
                доп.LastError = null;
                доп.FailedSerial = null;
                доп.Failures = 0;
                доп.NextAttemptAt = null;
                доп.DryRunSerial = null;
                break;

            case DeployOutcomes.DryRun:
                доп.DryRunSerial = выпуск.SerialNumber;
                доп.LastError = null;
                break;

            default:
                доп.Failures = Fingerprints.SameSerial(доп.FailedSerial, выпуск.SerialNumber) ? доп.Failures + 1 : 1;
                доп.FailedSerial = выпуск.SerialNumber;
                доп.NextAttemptAt = сейчас + DeployPlanner.Backoff(доп.Failures);
                доп.LastError = итог.Error;
                break;
        }
    }

    /// <summary>
    /// Письмо об итоге: удача — сразу, а из очереди — в сводке, когда очередь опустеет; неудача — сразу, но не чаще
    /// срока повтора на один выпуск.
    /// </summary>
    private async Task ОтчётAsync(
        KerioServer сервер, KerioDeployment установка, DeployResult итог, DateTimeOffset? следующая, bool дополнительный, CancellationToken cancellationToken)
    {
        var строки = new List<KerioNotifier.Row>
        {
            new("server", сервер.Name),
            new("address", сервер.BaseUrl),
            new("certificate", установка.CommonName),
            new("serial", установка.SerialNumber),
        };

        if (итог.NotAfter is { } до)
        {
            строки.Add(new("valid_until", до.ToUniversalTime().ToString("dd.MM.yyyy HH:mm 'UTC'", CultureInfo.InvariantCulture)));
        }

        if (итог.Sha256 is { } отпечаток)
        {
            строки.Add(new("fingerprint", Fingerprints.Pretty(отпечаток)));
        }

        строки.Add(new("trigger", Причина(установка.Trigger)));

        switch (итог.Outcome)
        {
            case DeployOutcomes.Succeeded:
                if (сервер.QueueReportSince is null && await settings.GetAsync(KerioSettings.AlertDeployed, cancellationToken))
                {
                    await notifier.SendAsync(
                        сервер,
                        KerioMail.Deployed,
                        $"Сертификат {установка.CommonName} установлен",
                        дополнительный
                            ? $"Сертификат лежит в хранилище Kerio и подаётся для своего имени; сертификат по умолчанию не изменён."
                              + (итог.PreviousKerioId is { } убран ? $" Прежние версии ({убран}) убраны, чтобы Kerio подавал новую." : string.Empty)
                            : "Прежний сертификат оставлен в хранилище Kerio для ручного возврата.",
                        строки,
                        установка.CommonName,
                        "deployed",
                        AlertLevel.Info,
                        cancellationToken);
                }

                break;

            case DeployOutcomes.DryRun:
                break;

            default:
                if (!await settings.GetAsync(KerioSettings.AlertDeployFailed, cancellationToken))
                {
                    return;
                }

                var ключ = KerioNotifier.Ключи.Отказ(установка.CommonName, установка.SerialNumber);
                var повтор = TimeSpan.FromHours(await settings.GetAsync(KerioSettings.AlertRepeatHours, cancellationToken));

                if (сервер.Alerts.TryGetValue(ключ, out var было) && (повтор == TimeSpan.Zero || clock.GetUtcNow() - было < повтор))
                {
                    return;
                }

                строки.Add(new("step", итог.FailedStep ?? "—"));
                строки.Add(new("reason", итог.Error ?? "—"));

                // ⚠ Повторит сам, только если сервису разрешено ставить этот сертификат без кнопки.
                var повторитСам = сервер.AutoDeploy && await settings.GetAsync(KerioSettings.AutoDeploy, cancellationToken);

                if (повторитСам && следующая is { } когда)
                {
                    строки.Add(new("next", когда.ToUniversalTime().ToString("dd.MM.yyyy HH:mm 'UTC'", CultureInfo.InvariantCulture)));
                }

                var откат = итог.Outcome == DeployOutcomes.RolledBack;
                var заголовок = откат
                    ? "🟠 Новый сертификат не встал — возвращён прежний"
                    : итог.RollbackConfirmed || итог.KerioId is null
                        ? $"🔴 Сертификат {установка.CommonName} не установлен"
                        : $"🔴 Сертификат {установка.CommonName} не установлен — нужна ручная проверка";

                if (await notifier.SendAsync(
                    сервер,
                    KerioMail.Failed,
                    заголовок,
                    откат
                        ? "Kerio принял новый сертификат, но сервер не начал подавать его на всех портах. Сервис вернул прежний и "
                          + "проверил, что он снова подаётся."
                          + (повторитСам ? " Установка повторится сама." : " Повторить — кнопкой «Обновить» у сертификата во вкладке «Обзор».")
                        : $"Установка {установка.CommonName} не удалась: {итог.Error}",
                    строки,
                    установка.CommonName,
                    откат ? DeployOutcomes.RolledBack : "deploy_failed",
                    откат ? AlertLevel.Warning : AlertLevel.Critical,
                    cancellationToken))
                {
                    сервер.Alerts[ключ] = clock.GetUtcNow();
                    await db.SaveChangesAsync(cancellationToken);
                }

                break;
        }
    }

    internal static string Причина(string trigger) => trigger switch
    {
        DeployTriggers.Renewal => "Продление в Key Manager Plus",
        DeployTriggers.Expiring => "Сертификат на сервере скоро истекает",
        DeployTriggers.Initial => "Первая установка после подключения",
        DeployTriggers.Manual => "Запрос администратора",
        DeployTriggers.Retry => "Повтор после неудачи",
        _ => trigger,
    };
}
