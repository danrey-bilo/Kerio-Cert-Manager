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
    /// <summary>Дольше любой установки (проверка TLS — до 10 минут); умерший процесс отпускает сервер сам.</summary>
    internal static readonly TimeSpan Срок = TimeSpan.FromMinutes(30);

    public async Task<bool> TryAcquireAsync(Guid serverId, Guid owner, CancellationToken cancellationToken = default)
    {
        var сейчас = clock.GetUtcNow();
        var до = сейчас + Срок;

        return await db.Set<KerioServer>()
            .Where(с => с.Id == serverId && (с.LeaseUntil == null || с.LeaseUntil < сейчас))
            .ExecuteUpdateAsync(set => set.SetProperty(с => с.LeaseUntil, до).SetProperty(с => с.LeaseOwner, owner), cancellationToken) == 1;
    }

    /// <summary>Продлить аренду перед долгим шагом: установок в проходе может быть несколько.</summary>
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

/// <summary>Проход синхронизации: опрос Key Manager Plus, затем каждый сервер — проверка, решение, установка, письма.</summary>
internal sealed class SyncCycle(
    AppDbContext db,
    ICertificateSource source,
    ServerInspection inspection,
    KerioDeployer deployer,
    KerioNotifier notifier,
    ServerLeases leases,
    ISettingsReader settings,
    IAuditWriter audit,
    TimeProvider clock,
    IServiceScopeFactory scopes,
    ILogger<SyncCycle> log)
{
    /// <summary>Текст записи об установке, которую оборвала остановка сервиса.</summary>
    internal const string Прервана = "Установка прервана остановкой сервиса.";

    /// <summary>Сколько серверов проход обрабатывает одновременно: зависший Kerio не задерживает остальные.</summary>
    internal const int Параллельно = 4;

    /// <summary>⚠ Kerio 10.0.6 через ~25 с после импорта или удаления перезапускает HTTPS и обрывает сессии.</summary>
    internal static TimeSpan ПаузаПослеИзменения { get; set; } = TimeSpan.FromSeconds(40);

    private Guid? _аренда;
    private DateTimeOffset? _изменено;

    public async Task RunAsync(CancellationToken cancellationToken = default)
    {
        await ЗакрытьБрошенныеAsync(cancellationToken);

        var опрос = await source.RefreshAsync(cancellationToken);

        if (опрос is { Configured: true, Succeeded: false })
        {
            log.LogWarning("kerio_sync_source_failed {Error}", опрос.Error);
        }

        var серверы = await db.Set<KerioServer>()
            .AsNoTracking()
            .Where(с => с.Enabled)
            .OrderBy(с => с.Name)
            .Select(с => с.Id)
            .ToListAsync(cancellationToken);

        // ⚠ Каждый сервер — в своей области: у прохода свой DbContext, а он не переносит параллельной работы.
        await Parallel.ForEachAsync(
            серверы,
            new ParallelOptions { MaxDegreeOfParallelism = Параллельно, CancellationToken = cancellationToken },
            async (id, token) =>
            {
                try
                {
                    await using var область = scopes.CreateAsyncScope();
                    await область.ServiceProvider.GetRequiredService<SyncCycle>().ProcessAsync(id, forceApi: false, token);
                }
                catch (Exception ошибка) when (!(ошибка is OperationCanceledException && token.IsCancellationRequested))
                {
                    log.LogError("kerio_sync_server_failed {Server} {Error}", id, SafeError.Describe(ошибка));
                }
            });
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

    /// <summary>Проверить сервер и, если пора, поставить сертификат.</summary>
    public async Task<ProcessOutcome> ProcessAsync(Guid serverId, bool forceApi, CancellationToken cancellationToken = default)
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
            _аренда = владелец;
            _изменено = null;

            var сервер = await db.Set<KerioServer>().FirstOrDefaultAsync(с => с.Id == serverId, cancellationToken);

            if (сервер is null)
            {
                return ProcessOutcome.Missing;
            }

            await ОбработатьAsync(сервер, forceApi || сервер.DeployRequestedAt is not null, cancellationToken);

            return ProcessOutcome.Done;
        }
        finally
        {
            _аренда = null;
            await leases.ReleaseAsync(serverId, владелец);
        }
    }

    /// <summary>Только проверка — TLS и вход в API, без решений и установки: кнопка «Проверить» и сохранение настроек.</summary>
    public async Task<ProcessOutcome> CheckAsync(Guid serverId, CancellationToken cancellationToken = default)
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

            ServerInspection.Apply(сервер, await inspection.InspectAsync(сервер, forceApi: true, cancellationToken), clock.GetUtcNow());
            await db.SaveChangesAsync(cancellationToken);

            return ProcessOutcome.Done;
        }
        finally
        {
            await leases.ReleaseAsync(serverId, владелец);
            db.ChangeTracker.Clear();
        }
    }

    private async Task ОбработатьAsync(KerioServer сервер, bool forceApi, CancellationToken cancellationToken)
    {
        var проверка = await inspection.InspectAsync(сервер, forceApi, cancellationToken);

        ServerInspection.Apply(сервер, проверка, clock.GetUtcNow());
        await db.SaveChangesAsync(cancellationToken);

        var предупреждения = new List<ServerAlert>(
            Связь(сервер, проверка, await settings.GetAsync(KerioSettings.AlertUnreachableChecks, cancellationToken)));

        var текущий = string.IsNullOrWhiteSpace(сервер.CommonName)
            ? null
            : await source.CurrentAsync(сервер.CommonName, cancellationToken);

        var план = DeployPlanner.Plan(
            сервер,
            проверка.Served,
            текущий,
            await settings.GetAsync(KerioSettings.AutoDeploy, cancellationToken),
            await settings.GetAsync(KerioSettings.WarningDays, cancellationToken),
            clock.GetUtcNow());

        предупреждения.AddRange(план.Alerts);

        var ручной = сервер.DeployRequestedAt is not null;

        if (план.Action == PlanAction.Adopt && проверка.Served is { } подан && план.Source is { } свой
            && await ВыпускKmpAsync(сервер, подан, свой, предупреждения, cancellationToken))
        {
            // ⚠ Сервер подаёт ровно сертификат из KMP (сверено по SHA-256) и прошёл рукопожатие — значит, владеет его ключом.
            if (сервер.TlsMode == TlsModes.Pin
                && сервер.InstalledSha256 is not null
                && string.Equals(сервер.TlsPinSha256, сервер.InstalledSha256, StringComparison.Ordinal))
            {
                сервер.TlsPinSha256 = подан.Sha256;
            }

            предупреждения.RemoveAll(а => а.Kind == AlertKinds.NotTrusted);

            сервер.PreviousSerials = InstalledHistory.Push(сервер.InstalledSerial, сервер.PreviousSerials, свой.SerialNumber);
            сервер.InstalledSerial = свой.SerialNumber;
            сервер.InstalledSha256 = подан.Sha256;
            сервер.InstalledNotBefore = подан.NotBefore;
            сервер.InstalledNotAfter = подан.NotAfter;
            сервер.InstalledAt ??= clock.GetUtcNow();
            сервер.FailedSerial = null;
            сервер.DeployFailures = 0;
            сервер.NextAttemptAt = null;
            сервер.LastDeployError = null;

            log.LogInformation("kerio_certificate_adopted {Server} {CommonName} {Serial}", сервер.Name, свой.CommonName, свой.SerialNumber);
        }
        else if (план.Action == PlanAction.Deploy && план.Source is { } выпуск)
        {
            if (проверка.ApiError is { } нельзя)
            {
                if (ручной)
                {
                    сервер.LastDeployError = $"Установка не начата: {нельзя}";
                }

                log.LogWarning("kerio_deploy_skipped_api {Server} {Error}", сервер.Name, нельзя);
            }
            else
            {
                var пробно = await settings.GetAsync(KerioSettings.DryRun, cancellationToken);

                if (пробно && !ручной && Fingerprints.SameSerial(сервер.DryRunSerial, выпуск.SerialNumber))
                {
                    log.LogDebug("kerio_dry_run_already {Server} {Serial}", сервер.Name, выпуск.SerialNumber);
                }
                else
                {
                    var итог = await УстановитьAsync(сервер, план, выпуск, пробно, cancellationToken);

                    if (итог.Outcome is DeployOutcomes.Succeeded or DeployOutcomes.RolledBack
                        || (итог.Outcome == DeployOutcomes.Failed && итог.KerioId is not null))
                    {
                        await ОбновитьХранилищеAsync(сервер, cancellationToken);
                    }
                }
            }
        }

        await ДопAsync(сервер, проверка, ручной, предупреждения, cancellationToken);

        if (Устаревший(сервер) is { } поИмени)
        {
            предупреждения.Add(поИмени);
        }

        if (план.ClearManualRequest || ручной)
        {
            сервер.DeployRequestedAt = null;
            сервер.DeployRequestedBy = null;
        }

        if (Просрочка(
                сервер,
                план,
                await settings.GetAsync(KerioSettings.AlertOverdueHours, cancellationToken),
                await settings.GetAsync(KerioSettings.WarningDays, cancellationToken),
                clock.GetUtcNow()) is { } просрочка)
        {
            предупреждения.Add(просрочка);
        }

        await db.SaveChangesAsync(cancellationToken);

        await notifier.AlertAsync(сервер, предупреждения, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
    }

    /// <summary>Подаёт ли сервер именно сертификат этого выпуска KMP — по SHA-256, а не по серийному номеру.</summary>
    private async Task<bool> ВыпускKmpAsync(
        KerioServer сервер, ServedCertificate подан, SourceCertificate выпуск, List<ServerAlert> предупреждения, CancellationToken cancellationToken)
    {
        try
        {
            var сертификаты = await source.FetchCertificatesAsync(выпуск.CommonName, выпуск.SerialNumber, cancellationToken);

            if (сертификаты.Any(der => Fingerprints.Sha256(der) == подан.Sha256))
            {
                return true;
            }
        }
        catch (CertificateSourceException ошибка)
        {
            log.LogWarning("kerio_adopt_deferred {Server} {Error}", сервер.Name, ошибка.Message);
            return false;
        }

        log.LogWarning("kerio_adopt_mismatch {Server} {Serial} {Sha256}", сервер.Name, подан.Serial, подан.Sha256);

        предупреждения.Add(new ServerAlert(
            AlertKinds.NotTrusted,
            AlertLevel.Critical,
            $"«{сервер.Name}» подаёт сертификат с номером выпуска KMP, но это другой сертификат",
            $"Серийный номер {подан.Serial} совпадает с выпуском {выпуск.CommonName} в Key Manager Plus, а отпечаток — нет. "
            + "Так выглядит подмена сервера или ручная установка чужого сертификата. Сервис его не признаёт и не доверяет ему."));

        return false;
    }

    /// <summary>Предупреждения о связи: не отвечает, пароль отвергнут, нет прав, сертификат не доверен.</summary>
    internal static IEnumerable<ServerAlert> Связь(KerioServer сервер, InspectionResult проверка, int порог)
    {
        if (проверка.Served is null && сервер.CheckFailures >= порог)
        {
            yield return new ServerAlert(
                AlertKinds.Unreachable,
                AlertLevel.Critical,
                $"Kerio «{сервер.Name}» не отвечает",
                $"Сервис не может соединиться с {сервер.BaseUrl}: {проверка.TlsError} Проверок подряд без ответа: {сервер.CheckFailures}.");
        }

        switch (проверка.ApiFailure)
        {
            case KerioFailure.Authentication when проверка.ApiChecked:
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

    /// <summary>Сертификаты своих имён: признать лежащие, поставить недостающие, предупредить о сроках.</summary>
    private async Task ДопAsync(
        KerioServer сервер, InspectionResult проверка, bool ручной, List<ServerAlert> предупреждения, CancellationToken cancellationToken)
    {
        if (сервер.Extras.Count == 0)
        {
            return;
        }

        var авто = await settings.GetAsync(KerioSettings.AutoDeploy, cancellationToken);
        var дней = await settings.GetAsync(KerioSettings.WarningDays, cancellationToken);
        var пробно = await settings.GetAsync(KerioSettings.DryRun, cancellationToken);
        var часов = await settings.GetAsync(KerioSettings.AlertOverdueHours, cancellationToken);
        var изменено = false;

        foreach (var доп in сервер.Extras.ToList())
        {
            if (string.Equals(доп.CommonName, сервер.CommonName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var текущий = await source.CurrentAsync(доп.CommonName, cancellationToken);
            var план = ExtraPlanner.Plan(сервер, доп, текущий, проверка.Store is not null, авто, дней, clock.GetUtcNow(), ручной);
            var своиПредупреждения = new List<ServerAlert>(план.Alerts);

            if (план.Action == PlanAction.Adopt && план.Present is { } лежит && план.Source is { } свой)
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
            else if (план.Action == PlanAction.Deploy && план.Source is { } выпуск)
            {
                if (проверка.ApiError is { } нельзя)
                {
                    if (ручной)
                    {
                        доп.LastError = $"Установка не начата: {нельзя}";
                    }
                }
                else if (!(пробно && !ручной && Fingerprints.SameSerial(доп.DryRunSerial, выпуск.SerialNumber)))
                {
                    var итог = await УстановитьДопAsync(сервер, доп, план.Trigger ?? DeployTriggers.Renewal, выпуск, пробно, cancellationToken);

                    изменено |= итог.Outcome != DeployOutcomes.DryRun && итог.KerioId is not null;

                    if (итог.Outcome == DeployOutcomes.Succeeded)
                    {
                        своиПредупреждения.Clear();
                    }
                }
            }

            предупреждения.AddRange(своиПредупреждения);

            if (ПросрочкаДоп(сервер, доп, план, часов, дней, clock.GetUtcNow()) is { } просрочка)
            {
                предупреждения.Add(просрочка);
            }
        }

        if (изменено)
        {
            await ОбновитьХранилищеAsync(сервер, cancellationToken);
        }
    }

    /// <summary>«Не обновился сам» для сертификата своего имени: отсчёт с продления, но не раньше добавления в список.</summary>
    internal static ServerAlert? ПросрочкаДоп(KerioServer сервер, ExtraCertificate доп, ExtraPlan план, int часов, int warningDays, DateTimeOffset now)
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

        var почему = доп.LastError is { } отказ
            ? $"Последняя установка не удалась: {отказ}"
            : сервер.LastCheckError is { } связь
                ? $"Сервер недоступен: {связь}"
                : план.Alerts.Any(а => а.Kind == AlertKinds.RenewalPending)
                    ? "Автоматическая установка выключена — нужна кнопка «Установить сейчас»."
                    : "Установка пока не прошла.";

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
    internal static ServerAlert? Просрочка(KerioServer сервер, DeployPlan план, int часов, int warningDays, DateTimeOffset now)
    {
        if (!план.Behind || план.Source is not { } нужный || Fingerprints.SameSerial(сервер.ServedSerial, нужный.SerialNumber))
        {
            return null;
        }

        var с = нужный.FirstSeenAt > сервер.CreatedAt ? нужный.FirstSeenAt : сервер.CreatedAt;
        var ждёт = now - с;

        if (ждёт < TimeSpan.FromHours(часов))
        {
            return null;
        }

        var почему = сервер.LastDeployError is { } отказ
            ? $"Последняя установка не удалась: {отказ}"
            : сервер.LastCheckError is { } связь
                ? $"Сервер недоступен: {связь}"
                : план.Alerts.Any(а => а.Kind == AlertKinds.RenewalPending)
                    ? "Автоматическая установка выключена — нужна кнопка «Установить сейчас»."
                    : "Установка пока не прошла.";

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
                    RequireRollbackTarget: !ручной && план.Trigger != DeployTriggers.Initial),
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

    /// <summary>Перед установкой: продлить аренду и, если хранилище в этом проходе уже менялось, дождаться перезапуска HTTPS.</summary>
    private async Task ПередУстановкойAsync(KerioServer сервер, CancellationToken cancellationToken)
    {
        if (_изменено is { } когда && clock.GetUtcNow() - когда < ПаузаПослеИзменения)
        {
            var ждать = ПаузаПослеИзменения - (clock.GetUtcNow() - когда);
            log.LogInformation("kerio_deploy_waits_restart {Server} {Seconds}", сервер.Name, (int)ждать.TotalSeconds);
            await Task.Delay(ждать, clock, cancellationToken);
        }

        if (_аренда is { } владелец)
        {
            await leases.ExtendAsync(сервер.Id, владелец);
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

        await ПередУстановкойAsync(сервер, cancellationToken);

        var установка = new KerioDeployment
        {
            Id = Guid.CreateVersion7(),
            ServerId = сервер.Id,
            ServerName = сервер.Name,
            Trigger = trigger,
            Outcome = DeployOutcomes.Running,
            StartedAt = clock.GetUtcNow(),
            ActorId = ручной ? сервер.DeployRequestedBy : null,
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
        установка.PreviousSha256 ??= итог.PreviousSha256;
        установка.FailedStep = итог.FailedStep;
        установка.Error = итог.Error;

        сервер.LastDeployAt = сейчас;
        применить(итог, сейчас);

        if (итог.Outcome != DeployOutcomes.DryRun && (итог.KerioId is not null || итог.PreviousKerioId is not null))
        {
            _изменено = сейчас;
        }

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

                var чужие = сервер.Extras.Select(д => д.FailedSerial).OfType<string>().Select(KerioNotifier.Ключи.Отказ).ToHashSet(StringComparer.Ordinal);

                foreach (var ключ in сервер.Alerts.Keys
                    .Where(к => к.StartsWith(KerioNotifier.Ключи.Установка, StringComparison.Ordinal) && !чужие.Contains(к)).ToList())
                {
                    сервер.Alerts.Remove(ключ);
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
                сервер.Alerts.Remove(KerioNotifier.Ключи.Отказ(выпуск.SerialNumber));
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

    /// <summary>Письмо об итоге: удача — всегда; неудача — не чаще срока повтора на один выпуск.</summary>
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
                if (await settings.GetAsync(KerioSettings.AlertDeployed, cancellationToken))
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

                var ключ = KerioNotifier.Ключи.Отказ(установка.SerialNumber);
                var повтор = TimeSpan.FromHours(await settings.GetAsync(KerioSettings.AlertRepeatHours, cancellationToken));

                if (сервер.Alerts.TryGetValue(ключ, out var было) && (повтор == TimeSpan.Zero || clock.GetUtcNow() - было < повтор))
                {
                    return;
                }

                строки.Add(new("step", итог.FailedStep ?? "—"));
                строки.Add(new("reason", итог.Error ?? "—"));

                if (следующая is { } когда && установка.Trigger != DeployTriggers.Manual)
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
                          + "проверил, что он снова подаётся. Установка повторится сама."
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
