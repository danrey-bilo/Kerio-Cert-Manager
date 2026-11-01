using System.Net;
using System.Net.Sockets;
using KerioCertManager.Modules.Kerio.Certificates;
using KerioCertManager.Modules.Kerio.Client;
using KerioCertManager.Modules.Kerio.Contracts;
using KerioCertManager.Modules.Kerio.Domain;
using KerioCertManager.Modules.Kerio.Notifications;
using KerioCertManager.Modules.Kerio.Secrets;
using KerioCertManager.Modules.Kerio.Settings;
using KerioCertManager.Modules.Kerio.Sync;
using KerioCertManager.Modules.KeyManager.Contracts;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Modules.Core.Contracts.Audit;
using Modules.Core.Contracts.Authz;
using Modules.Core.Contracts.Settings;
using Modules.Core.Data;
using Modules.Core.Errors;
using Npgsql;

namespace KerioCertManager.Modules.Kerio.Api;

/// <summary>Серверы Kerio Connect: подключение, правка, проверка, установка, история.</summary>
[ApiController]
[AuthenticatedEndpoint("ролей в проекте нет: каждая учётная запись — администратор серверов Kerio")]
[Route("api/kerio")]
internal sealed class ServersController(
    AppDbContext db,
    ServerPasswords passwords,
    IKerioConnector connector,
    ITlsInspector tls,
    ICertificateSource source,
    SyncCycle cycle,
    SourceWakeup wakeup,
    ISettingsReader settings,
    ICurrentUser current,
    IAuditWriter audit,
    KerioModuleOptions options,
    TimeProvider clock) : ControllerBase
{
    private const int ПотолокПароля = 1024;

    /// <summary>Насколько свежим должен быть код второго фактора для удаления сервера.</summary>
    internal static readonly TimeSpan СвежийКодДляУдаления = TimeSpan.FromMinutes(2);

    /// <summary>Пробное оповещение — не чаще раза в столько: письма вида Kerio сняты с общего предела почты.</summary>
    internal static readonly TimeSpan ПаузаПробы = TimeSpan.FromMinutes(1);

    /// <summary>Порты служб Kerio с TLS сразу (не STARTTLS): HTTPS, IMAPS, POP3S, SMTPS, LDAPS.</summary>
    private static readonly int[] ПортыСлужб = [443, 993, 995, 465, 636];

    /// <summary>Порты Kerio без TLS или со STARTTLS: HTTP, SMTP, Submission, POP3, IMAP, LDAP, NNTP, XMPP, Sieve, HTTP запасной.</summary>
    private static readonly HashSet<int> ПортыБезTls = [25, 80, 110, 119, 143, 389, 587, 4190, 5222, 8800];

    /// <summary>Сводка «Обзора»: серверы со статусом и хранилищем, связь с Key Manager Plus, режимы.</summary>
    [HttpGet("overview")]
    public async Task<OverviewOut> Overview(CancellationToken cancellationToken)
    {
        var выпуски = await source.ListAsync(cancellationToken);

        return new OverviewOut(
            await СписокAsync(выпуски, true, cancellationToken),
            new SourceStatusOut(выпуски.Count),
            await settings.GetAsync(KerioSettings.AutoDeploy, cancellationToken),
            await settings.GetAsync(KerioSettings.DryRun, cancellationToken),
            await settings.GetAsync(KerioSettings.WarningDays, cancellationToken),
            await settings.GetAsync(CertificateSourceSettings.IntervalMinutes, cancellationToken));
    }

    /// <summary>Все серверы со статусом; хранилище Kerio — только с <c>?include=store</c>.</summary>
    [HttpGet("servers")]
    public async Task<IReadOnlyList<ServerOut>> List([FromQuery] string? include, CancellationToken cancellationToken) =>
        await СписокAsync(await source.ListAsync(cancellationToken), include == "store", cancellationToken);

    /// <summary>Сервер со статусом.</summary>
    [HttpGet("servers/{id:guid}")]
    public async Task<ServerOut> Get(Guid id, CancellationToken cancellationToken) =>
        await ОдинAsync(await НайтиAsync(id, tracked: false, cancellationToken), cancellationToken);

    /// <summary>Что подаёт адрес по TLS — до подключения сервера, для подтверждения отпечатка.</summary>
    [HttpPost("servers/probe")]
    public async Task<ProbeOut> Probe([FromBody] ProbeIn body, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(body);

        var адрес = await АдресAsync(body.BaseUrl, cancellationToken);

        ServedCertificate подан;

        try
        {
            подан = await tls.InspectAsync(адрес.Host, адрес.Port, cancellationToken);
        }
        catch (TlsException ошибка)
        {
            throw Недоступен(ошибка.Message);
        }

        using var лист = System.Security.Cryptography.X509Certificates.X509Certificate2.CreateFromPem(подан.Pem);

        var имя = ИмяTls(body.TlsServerName);
        var поDns = Uri.CheckHostName(адрес.Host) == UriHostNameType.Dns;
        var поИмени = подан.ChainTrusted && (имя is not null ? Fingerprints.Covers(лист, имя) : поDns && Fingerprints.Covers(лист, адрес.Host));

        var предложить = имя ?? (подан.ChainTrusted && !поDns
            ? подан.DnsNames.FirstOrDefault(и => !и.StartsWith("*.", StringComparison.Ordinal)) ?? подан.DnsNames.FirstOrDefault() ?? подан.Subject
            : null);

        var порты = await Task.WhenAll(ПортыСлужб.Where(п => п != адрес.Port).Select(async порт =>
        {
            using var срок = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            срок.CancelAfter(TimeSpan.FromSeconds(4));

            try
            {
                return (await tls.InspectAsync(адрес.Host, порт, срок.Token)).Sha256 == подан.Sha256 ? порт : 0;
            }
            catch (Exception ошибка) when (ошибка is TlsException or OperationCanceledException)
            {
                return 0;
            }
        }));

        return new ProbeOut(
            адрес.Host,
            адрес.Port,
            подан.Sha256,
            подан.Subject,
            подан.Issuer,
            подан.Serial,
            подан.NotBefore,
            подан.NotAfter,
            подан.DnsNames,
            подан.SelfSigned,
            подан.ChainTrusted,
            поИмени || (подан.ChainTrusted && предложить is not null) ? TlsModes.Ca : TlsModes.Pin,
            предложить,
            [.. порты.Where(п => п > 0)],
            подан.Pem);
    }

    /// <summary>Войти в Kerio и показать хранилище сертификатов — до сохранения или с сохранённым паролем.</summary>
    [HttpPost("servers/test")]
    public async Task<TestOut> Test([FromBody] TestIn body, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(body);

        KerioServer сервер;
        string? пароль = null;

        if (body.ServerId is { } id)
        {
            сервер = await НайтиAsync(id, tracked: false, cancellationToken);

            // ⚠ Kerio уже отверг сохранённый пароль: ещё один вход тем же паролем приближает блокировку учётной записи.
            if (сервер.AuthRejected)
            {
                return new TestOut(
                    false,
                    "Kerio прежде отверг сохранённый пароль — сервис не входит с ним, чтобы не заблокировать учётную запись. "
                    + "Задайте пароль заново или нажмите «Проверить» на странице сервера.",
                    null, null, null, false, [], []);
            }
        }
        else
        {
            сервер = new KerioServer
            {
                Id = Guid.Empty,
                Name = "test",
                BaseUrl = (await АдресAsync(body.BaseUrl, cancellationToken)).GetLeftPart(UriPartial.Authority),
                Username = Логин(body.Username),
                TlsMode = Режим(body.TlsMode),
                TlsPinSha256 = Пин(body.TlsMode, body.TlsPinSha256),
                TlsServerName = ИмяTls(body.TlsServerName),
            };

            пароль = Пароль(body.Password);
        }

        var выпуски = await source.ListAsync(cancellationToken);

        try
        {
            await using var сессия = пароль is null
                ? await connector.OpenAsync(сервер, cancellationToken)
                : await connector.OpenAsync(сервер, пароль, cancellationToken);

            var кто = await сессия.WhoAmIAsync(cancellationToken);
            var версия = await сессия.VersionAsync(cancellationToken);
            var хранилище = await сессия.CertificatesAsync(cancellationToken);

            var активный = хранилище.FirstOrDefault(с => с.IsDefault && !string.IsNullOrEmpty(с.Subject))?.Subject;
            var имена = выпуски.Select(в => в.CommonName).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

            var подходящие = активный is null
                ? []
                : имена.Where(cn => string.Equals(cn, активный, StringComparison.OrdinalIgnoreCase))
                    .Concat(имена.Where(cn => cn.StartsWith("*.", StringComparison.Ordinal)
                        && активный.EndsWith(cn[1..], StringComparison.OrdinalIgnoreCase)
                        && !активный[..^(cn.Length - 1)].Contains('.', StringComparison.Ordinal)))
                    .ToList();

            return new TestOut(
                кто.Role == ServerInspection.FullAdmin,
                кто.Role == ServerInspection.FullAdmin
                    ? null
                    : $"Роль учётной записи — {кто.Role}: для установки сертификатов нужна роль полного администратора.",
                версия,
                кто.LoginName,
                кто.Role,
                кто.Role == ServerInspection.FullAdmin,
                [.. хранилище.Select(с => new KerioCertificateOut(с.Id, с.Type, с.Subject, с.Issuer, с.ValidTo, с.IsSelfSigned))],
                подходящие);
        }
        catch (KerioException ошибка)
        {
            return new TestOut(false, ошибка.Message, null, null, null, false, [], []);
        }
    }

    /// <summary>Подключить сервер.</summary>
    [HttpPost("servers")]
    [RequireRecentSecondFactor]
    public async Task<IActionResult> Create([FromBody] ServerIn body, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(body);

        var сейчас = clock.GetUtcNow();
        var (поУмолчанию, свои) = CertificateNames.Check(
            body.CommonName, body.ExtraCommonNames, await source.ListAsync(cancellationToken), []);

        var сервер = new KerioServer
        {
            Id = Guid.CreateVersion7(),
            Name = Имя(body.Name),
            BaseUrl = (await АдресAsync(body.BaseUrl, cancellationToken)).GetLeftPart(UriPartial.Authority),
            Username = Логин(body.Username),
            TlsMode = Режим(body.TlsMode),
            TlsPinSha256 = Пин(body.TlsMode, body.TlsPinSha256),
            TlsServerName = ИмяTls(body.TlsServerName),
            CommonName = поУмолчанию,
            Extras = [.. свои.Select(имя => new ExtraCertificate { CommonName = имя, AddedAt = сейчас })],
            AutoDeploy = body.AutoDeploy ?? true,
            CleanupOld = body.CleanupOld ?? false,
            VerifyPorts = Порты(body.VerifyPorts),
            NotifyEmails = Адреса(body.NotifyEmails),
            Enabled = body.Enabled ?? true,
            CreatedAt = сейчас,
            CreatedBy = current.Id,
            UpdatedAt = сейчас,
            UpdatedBy = current.Id,
        };

        passwords.Put(сервер, Пароль(body.Password));

        await СохранитьAsync(
            async token =>
            {
                db.Set<KerioServer>().Add(сервер);

                audit.Write(new AuditEntry(
                    KerioActions.ServerCreated,
                    ResourceType: KerioActions.ServerResource,
                    ResourceId: сервер.Id,
                    Changes: Снимок(сервер)));

                await db.SaveChangesAsync(token);
            },
            cancellationToken);

        await cycle.CheckAsync(сервер.Id, cancellationToken);
        wakeup.Signal();

        return CreatedAtAction(nameof(Get), new { id = сервер.Id }, await ОдинAsync(await НайтиAsync(сервер.Id, false, cancellationToken), cancellationToken));
    }

    /// <summary>Изменить сервер.</summary>
    [HttpPatch("servers/{id:guid}")]
    [RequireRecentSecondFactor]
    public async Task<ServerOut> Update(Guid id, [FromBody] ServerPatch body, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(body);

        if (body.BaseUrl is not null)
        {
            await АдресAsync(body.BaseUrl, cancellationToken);
        }

        var сертификатыМеняются = body.CommonName is not null || body.ClearCommonName == true || body.ExtraCommonNames is not null;
        var выпуски = сертификатыМеняются ? await source.ListAsync(cancellationToken) : [];

        var сервер = await ИзменитьAsync(
            id,
            сервер =>
            {
                var было = Снимок(сервер);

                if (body.Name is not null)
                {
                    сервер.Name = Имя(body.Name);
                }

                var адрес = body.BaseUrl is null ? сервер.BaseUrl : Адрес(body.BaseUrl).GetLeftPart(UriPartial.Authority);
                var логин = body.Username is null ? сервер.Username : Логин(body.Username);
                var доступСменён =
                    !string.Equals(ServerPasswords.NormalizeUrl(адрес), ServerPasswords.NormalizeUrl(сервер.BaseUrl), StringComparison.Ordinal)
                    || !string.Equals(логин, сервер.Username, StringComparison.OrdinalIgnoreCase);

                if (доступСменён && string.IsNullOrEmpty(body.Password))
                {
                    throw Неверно("password", "Адрес или логин сменились — введите пароль заново: прежний привязан к прежнему адресу и логину.");
                }

                // ⚠ Поставленный сервисом сертификат доверен при входе сверх режима — но только по тому адресу, куда его ставили.
                if (!string.Equals(ServerPasswords.NormalizeUrl(адрес), ServerPasswords.NormalizeUrl(сервер.BaseUrl), StringComparison.Ordinal))
                {
                    сервер.InstalledSerial = null;
                    сервер.InstalledSha256 = null;
                    сервер.InstalledNotBefore = null;
                    сервер.InstalledNotAfter = null;
                    сервер.InstalledKerioId = null;
                    сервер.PreviousSerials = [];
                    сервер.FailedSerial = null;
                    сервер.DeployFailures = 0;
                    сервер.NextAttemptAt = null;
                    сервер.DryRunSerial = null;
                }

                сервер.BaseUrl = адрес;
                сервер.Username = логин;

                if (!string.IsNullOrEmpty(body.Password))
                {
                    passwords.Put(сервер, Пароль(body.Password));
                    сервер.AuthRejected = false;
                }

                var (поУмолчанию, свои) = сертификатыМеняются
                    ? CertificateNames.Check(
                        body.ClearCommonName == true ? null : body.CommonName ?? сервер.CommonName,
                        body.ExtraCommonNames ?? [.. сервер.Extras.Select(д => д.CommonName)],
                        выпуски,
                        сервер.ManagedNames())
                    : (сервер.CommonName, [.. сервер.Extras.Select(д => д.CommonName)]);

                if (body.ExtraCommonNames is not null)
                {
                    сервер.Extras =
                    [
                        .. свои
                            .Where(имя => !string.Equals(имя, поУмолчанию, StringComparison.OrdinalIgnoreCase))
                            .Select(имя => сервер.Extras.FirstOrDefault(д => string.Equals(д.CommonName, имя, StringComparison.OrdinalIgnoreCase))
                                ?? new ExtraCertificate { CommonName = имя, AddedAt = clock.GetUtcNow() }),
                    ];
                }

                if (body.ClearCommonName == true)
                {
                    сервер.CommonName = null;
                }
                else if (body.CommonName is not null)
                {
                    var имя = поУмолчанию ?? ИмяСертификата(body.CommonName);

                    if (!string.Equals(имя, сервер.CommonName, StringComparison.OrdinalIgnoreCase))
                    {
                        сервер.InstalledSerial = null;
                        сервер.InstalledSha256 = null;
                        сервер.InstalledNotBefore = null;
                        сервер.InstalledNotAfter = null;
                        сервер.FailedSerial = null;
                        сервер.DeployFailures = 0;
                        сервер.NextAttemptAt = null;
                        сервер.LastDeployError = null;
                        сервер.DryRunSerial = null;
                    }

                    сервер.CommonName = имя;
                }

                сервер.AutoDeploy = body.AutoDeploy ?? сервер.AutoDeploy;
                сервер.CleanupOld = body.CleanupOld ?? сервер.CleanupOld;
                сервер.Enabled = body.Enabled ?? сервер.Enabled;

                if (body.VerifyPorts is not null)
                {
                    сервер.VerifyPorts = Порты(body.VerifyPorts);
                }

                if (body.NotifyEmails is not null)
                {
                    сервер.NotifyEmails = Адреса(body.NotifyEmails);
                }

                return new AuditEntry(
                    KerioActions.ServerUpdated,
                    ResourceType: KerioActions.ServerResource,
                    ResourceId: сервер.Id,
                    Changes: Разница(было, Снимок(сервер), !string.IsNullOrEmpty(body.Password)));
            },
            cancellationToken);

        wakeup.Signal();

        return await ОдинAsync(сервер, cancellationToken);
    }

    /// <summary>Задать пароль учётной записи Kerio.</summary>
    [HttpPut("servers/{id:guid}/password")]
    [RequireRecentSecondFactor]
    public async Task<IActionResult> SetPassword(Guid id, [FromBody] PasswordIn body, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(body);

        var пароль = Пароль(body.Password);

        await ИзменитьAsync(
            id,
            сервер =>
            {
                passwords.Put(сервер, пароль);
                сервер.AuthRejected = false;

                return new AuditEntry(
                    KerioActions.PasswordSet,
                    ResourceType: KerioActions.ServerResource,
                    ResourceId: сервер.Id,
                    Changes: new Dictionary<string, object?>(StringComparer.Ordinal) { ["server"] = сервер.Name });
            },
            cancellationToken);

        await cycle.CheckAsync(id, cancellationToken);

        return NoContent();
    }

    /// <summary>Изменить доверие к сертификату Kerio: центр сертификации или закреплённый отпечаток.</summary>
    [HttpPut("servers/{id:guid}/trust")]
    [RequireRecentSecondFactor]
    public async Task<ServerOut> SetTrust(Guid id, [FromBody] TrustIn body, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(body);

        var режим = Режим(body.TlsMode);
        var пин = Пин(body.TlsMode, body.TlsPinSha256);
        var имя = ИмяTls(body.TlsServerName);

        await ИзменитьAsync(
            id,
            сервер =>
            {
                var было = (сервер.TlsMode, сервер.TlsPinSha256, сервер.TlsServerName);

                сервер.TlsMode = режим;
                сервер.TlsPinSha256 = пин;
                сервер.TlsServerName = имя;

                return new AuditEntry(
                    KerioActions.TrustUpdated,
                    ResourceType: KerioActions.ServerResource,
                    ResourceId: сервер.Id,
                    Changes: new Dictionary<string, object?>(StringComparer.Ordinal)
                    {
                        ["server"] = сервер.Name,
                        ["tls_mode"] = new[] { было.TlsMode, сервер.TlsMode },
                        ["tls_pin_sha256"] = new[] { было.TlsPinSha256, сервер.TlsPinSha256 },
                        ["tls_server_name"] = new[] { было.TlsServerName, сервер.TlsServerName },
                    });
            },
            cancellationToken);

        await cycle.CheckAsync(id, cancellationToken);

        return await ОдинAsync(await НайтиAsync(id, false, cancellationToken), cancellationToken);
    }

    /// <summary>Отключить сервер от сервиса; история установок остаётся.</summary>
    [HttpDelete("servers/{id:guid}")]
    [RequireRecentSecondFactor]
    public async Task<IActionResult> Delete(Guid id, CancellationToken cancellationToken)
    {
        if (current.StepUpAt is not { } когда || clock.GetUtcNow() - когда > СвежийКодДляУдаления)
        {
            throw new AppException(
                ErrorCodes.SecondFactorRequired,
                "Удаление сервера подтверждается свежим кодом второго фактора.",
                HttpStatusCode.Forbidden,
                new Dictionary<string, object?>(StringComparer.Ordinal) { ["window_minutes"] = (int)СвежийКодДляУдаления.TotalMinutes });
        }

        await СохранитьAsync(
            async token =>
            {
                var сервер = await ЗаблокироватьAsync(id, token) ?? throw НетСервера();

                if (сервер.LeaseUntil is { } аренда && аренда > clock.GetUtcNow())
                {
                    throw Занят();
                }

                db.Set<KerioServer>().Remove(сервер);

                audit.Write(new AuditEntry(
                    KerioActions.ServerDeleted,
                    ResourceType: KerioActions.ServerResource,
                    ResourceId: сервер.Id,
                    Changes: Снимок(сервер)));

                await db.SaveChangesAsync(token);
            },
            cancellationToken);

        return NoContent();
    }

    /// <summary>Проверить сервер сейчас: TLS и вход в API, даже если Kerio прежде отверг пароль.</summary>
    [HttpPost("servers/{id:guid}/check")]
    public async Task<ServerOut> Check(Guid id, CancellationToken cancellationToken)
    {
        var итог = await cycle.CheckAsync(id, cancellationToken);

        if (итог == ProcessOutcome.Missing)
        {
            throw НетСервера();
        }

        if (итог == ProcessOutcome.Busy)
        {
            throw Занят();
        }

        wakeup.Signal();

        return await ОдинAsync(await НайтиAsync(id, false, cancellationToken), cancellationToken);
    }

    /// <summary>Установить текущий выпуск из Key Manager Plus сейчас; имя, которое сервис ещё не ведёт, берётся под управление.</summary>
    [HttpPost("servers/{id:guid}/deploy")]
    [RequireRecentSecondFactor]
    public async Task<IActionResult> Deploy(
        Guid id,
        [FromBody(EmptyBodyBehavior = Microsoft.AspNetCore.Mvc.ModelBinding.EmptyBodyBehavior.Allow)] DeployIn? body,
        CancellationToken cancellationToken)
    {
        var найден = await НайтиAsync(id, tracked: false, cancellationToken);
        var просят = body?.CommonName?.Trim();
        var выбран = string.IsNullOrEmpty(просят)
            ? null
            : найден.ManagedNames().FirstOrDefault(н => string.Equals(н, просят, StringComparison.OrdinalIgnoreCase));
        var имя = выбран ?? (string.IsNullOrEmpty(просят) ? найден.CommonName : просят);
        var свежий = string.IsNullOrWhiteSpace(имя) ? null : await source.CurrentAsync(имя, cancellationToken);
        var взять = выбран is null && !string.IsNullOrEmpty(просят) ? свежий : null;

        if (!string.IsNullOrWhiteSpace(имя) && свежий is null)
        {
            throw new AppException(
                KerioErrorCodes.Unavailable,
                $"В последнем опросе Key Manager Plus нет сертификата {имя}.",
                HttpStatusCode.Conflict,
                new Dictionary<string, object?>(StringComparer.Ordinal) { ["reason"] = "Проверьте связь с Key Manager Plus." });
        }

        await ИзменитьAsync(
            id,
            сервер =>
            {
                if (взять is not null)
                {
                    // Подаётся по умолчанию, а сертификат по умолчанию не выбран — обновляется он; иначе — сертификат имени.
                    if (сервер.CommonName is null && string.Equals(сервер.ServedSubject, взять.CommonName, StringComparison.OrdinalIgnoreCase))
                    {
                        сервер.CommonName = взять.CommonName;
                    }
                    else if (сервер.Extras.Count >= KerioServer.MaxExtras)
                    {
                        throw Неверно("common_name", $"Сертификатов по именам уже {KerioServer.MaxExtras} — больше нельзя.");
                    }
                    else
                    {
                        сервер.Extras = [.. сервер.Extras, new ExtraCertificate { CommonName = взять.CommonName, AddedAt = clock.GetUtcNow() }];
                    }
                }

                if (string.IsNullOrWhiteSpace(сервер.CommonName) && сервер.Extras.Count == 0)
                {
                    throw Неверно(CertificateNames.Field, "Сначала выберите сертификаты Key Manager Plus для этого сервера.");
                }

                if (!сервер.Enabled)
                {
                    throw Неверно("enabled", "Сервер отключён — включите его, чтобы установить сертификат.");
                }

                if (сервер.Password is null)
                {
                    throw Неверно("password", "Задайте пароль учётной записи Kerio.");
                }

                сервер.DeployRequestedAt = clock.GetUtcNow();
                сервер.DeployRequestedBy = current.Id;
                сервер.DeployRequestedName = выбран ?? взять?.CommonName;

                return new AuditEntry(
                    KerioActions.DeployRequested,
                    ResourceType: KerioActions.ServerResource,
                    ResourceId: сервер.Id,
                    Changes: new Dictionary<string, object?>(StringComparer.Ordinal)
                    {
                        ["server"] = сервер.Name,
                        ["common_name"] = сервер.CommonName,
                        ["extras"] = string.Join(", ", сервер.Extras.Select(д => д.CommonName)),
                        ["requested"] = сервер.DeployRequestedName,
                        ["linked"] = взять?.CommonName,
                    });
            },
            cancellationToken,
            touch: false);

        wakeup.Signal();

        return Accepted();
    }

    /// <summary>Установки сервера, новые первыми.</summary>
    [HttpGet("servers/{id:guid}/deployments")]
    public async Task<IReadOnlyList<DeploymentOut>> ServerDeployments(Guid id, [FromQuery] int? limit, CancellationToken cancellationToken)
    {
        var сколько = Math.Clamp(limit ?? 20, 1, 200);

        var строки = await db.Set<KerioDeployment>()
            .AsNoTracking()
            .Where(у => у.ServerId == id)
            .OrderByDescending(у => у.StartedAt)
            .Take(сколько)
            .ToListAsync(cancellationToken);

        return [.. строки.Select(у => ServerView.Of(у, steps: true))];
    }

    /// <summary>Журнал установок по всем серверам, новые первыми; курсор — <c>before</c> (время начала).</summary>
    [HttpGet("deployments")]
    public async Task<IReadOnlyList<DeploymentOut>> Deployments(
        [FromQuery] int? limit,
        [FromQuery] DateTimeOffset? before,
        [FromQuery] string? outcome,
        [FromQuery] bool? steps,
        CancellationToken cancellationToken)
    {
        var сколько = Math.Clamp(limit ?? 50, 1, 200);
        var запрос = db.Set<KerioDeployment>().AsNoTracking();

        if (before is { } до)
        {
            запрос = запрос.Where(у => у.StartedAt < до);
        }

        if (!string.IsNullOrWhiteSpace(outcome))
        {
            запрос = запрос.Where(у => у.Outcome == outcome);
        }

        var строки = await запрос.OrderByDescending(у => у.StartedAt).Take(сколько).ToListAsync(cancellationToken);

        return [.. строки.Select(у => ServerView.Of(у, steps: steps == true))];
    }

    /// <summary>Установка с шагами.</summary>
    [HttpGet("deployments/{id:guid}")]
    public async Task<DeploymentOut> Deployment(Guid id, CancellationToken cancellationToken) =>
        ServerView.Of(
            await db.Set<KerioDeployment>().AsNoTracking().FirstOrDefaultAsync(у => у.Id == id, cancellationToken)
                ?? throw new AppException(ErrorCodes.NotFound, "Такой установки нет.", HttpStatusCode.NotFound),
            steps: true);

    /// <summary>Журнал оповещений: что и кому ушло письмом, новые сверху.</summary>
    [HttpGet("notifications")]
    public async Task<IReadOnlyList<NotificationOut>> Notifications(
        [FromQuery] int? limit, [FromQuery] DateTimeOffset? before, [FromQuery(Name = "server_id")] Guid? serverId, CancellationToken cancellationToken)
    {
        var запрос = db.Set<KerioNotification>().AsNoTracking();

        if (before is { } до)
        {
            запрос = запрос.Where(о => о.At < до);
        }

        if (serverId is { } сервер)
        {
            запрос = запрос.Where(о => о.ServerId == сервер);
        }

        var строки = await запрос
            .OrderByDescending(о => о.At)
            .Take(Math.Clamp(limit ?? 50, 1, 200))
            .ToListAsync(cancellationToken);

        return [.. строки.Select(о => new NotificationOut(о.Id, о.At, о.ServerId, о.ServerName, о.Event, о.Level, о.Title, о.Recipients, о.Queued))];
    }

    /// <summary>Пробное оповещение общим получателям.</summary>
    [HttpPost("notifications/test")]
    public async Task<NotificationTestOut> TestNotification([FromServices] KerioNotifier notifier, CancellationToken cancellationToken)
    {
        var сейчас = clock.GetUtcNow();

        if (await db.Set<KerioNotification>().AnyAsync(о => о.Event == "test" && о.At > сейчас - ПаузаПробы, cancellationToken))
        {
            throw new AppException(ErrorCodes.RateLimited, "Пробное письмо уже ушло — следующее можно через минуту.", HttpStatusCode.TooManyRequests);
        }

        var кому = await notifier.TestAsync(current.Id, cancellationToken);

        if (кому.Count == 0)
        {
            throw new AppException(
                ErrorCodes.MailNotConfigured,
                "Письмо не ушло: проверьте сервер SMTP в настройках почты и получателей оповещений.",
                HttpStatusCode.Conflict);
        }

        return new NotificationTestOut(кому);
    }

    private async Task<IReadOnlyList<ServerOut>> СписокAsync(
        IReadOnlyList<SourceCertificate> выпуски, bool хранилище, CancellationToken cancellationToken)
    {
        var текущие = ServerView.Current(выпуски);
        var поНомеру = хранилище ? ServerView.BySerial(выпуски) : null;
        var дней = await settings.GetAsync(KerioSettings.WarningDays, cancellationToken);
        var пробно = await settings.GetAsync(KerioSettings.DryRun, cancellationToken);
        var сейчас = clock.GetUtcNow();

        var серверы = await db.Set<KerioServer>().AsNoTracking().OrderBy(с => с.Name).ToListAsync(cancellationToken);

        return [.. серверы.Select(с => ServerView.Of(с, текущие, дней, сейчас, пробно, поНомеру))];
    }

    private async Task<ServerOut> ОдинAsync(KerioServer сервер, CancellationToken cancellationToken)
    {
        var выпуски = await source.ListAsync(cancellationToken);

        return ServerView.Of(
            сервер,
            ServerView.Current(выпуски),
            await settings.GetAsync(KerioSettings.WarningDays, cancellationToken),
            clock.GetUtcNow(),
            await settings.GetAsync(KerioSettings.DryRun, cancellationToken),
            ServerView.BySerial(выпуски));
    }

    private async Task<KerioServer> НайтиAsync(Guid id, bool tracked, CancellationToken cancellationToken)
    {
        var запрос = tracked ? db.Set<KerioServer>() : db.Set<KerioServer>().AsNoTracking();

        return await запрос.FirstOrDefaultAsync(с => с.Id == id, cancellationToken) ?? throw НетСервера();
    }

    /// <summary>Загрузить сервер, изменить и записать строку журнала — внутри одной транзакции.</summary>
    private async Task<KerioServer> ИзменитьAsync(
        Guid id, Func<KerioServer, AuditEntry> правка, CancellationToken cancellationToken, bool touch = true)
    {
        KerioServer? итог = null;

        await СохранитьAsync(
            async token =>
            {
                var сервер = await ЗаблокироватьAsync(id, token) ?? throw НетСервера();

                // ⚠ Проход держит сервер минутами и сохраняет его в конце: правка посреди него молча пропала бы.
                if (touch && сервер.LeaseUntil is { } аренда && аренда > clock.GetUtcNow())
                {
                    throw Занят();
                }

                var строка = правка(сервер);

                if (touch)
                {
                    сервер.UpdatedAt = clock.GetUtcNow();
                    сервер.UpdatedBy = current.Id;
                }

                audit.Write(строка);
                await db.SaveChangesAsync(token);

                итог = сервер;
            },
            cancellationToken);

        return итог!;
    }

    /// <summary>Строка сервера с блокировкой до конца транзакции: проход не возьмёт аренду посреди правки.</summary>
    private async Task<KerioServer?> ЗаблокироватьAsync(Guid id, CancellationToken cancellationToken) =>
        (await db.Set<KerioServer>().FromSql($"SELECT * FROM kerio_servers WHERE id = {id} FOR UPDATE").ToListAsync(cancellationToken))
            .FirstOrDefault();

    /// <summary>Транзакция с журналом; занятое имя — <c>409</c>.</summary>
    private async Task СохранитьAsync(Func<CancellationToken, Task> действие, CancellationToken cancellationToken)
    {
        try
        {
            await db.InTransactionAsync(действие, cancellationToken);
        }
        catch (DbUpdateException ошибка) when (ошибка.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            throw new AppException(KerioErrorCodes.NameTaken, "Сервер с таким именем уже подключён.", HttpStatusCode.Conflict,
                new Dictionary<string, object?>(StringComparer.Ordinal) { ["field"] = "name" });
        }
    }

    private static Dictionary<string, object?> Снимок(KerioServer с) => new(StringComparer.Ordinal)
    {
        ["name"] = с.Name,
        ["base_url"] = с.BaseUrl,
        ["username"] = с.Username,
        ["tls_mode"] = с.TlsMode,
        ["tls_pin_sha256"] = с.TlsPinSha256,
        ["tls_server_name"] = с.TlsServerName,
        ["common_name"] = с.CommonName,
        ["extras"] = string.Join(", ", с.Extras.Select(д => д.CommonName)),
        ["auto_deploy"] = с.AutoDeploy,
        ["cleanup_old"] = с.CleanupOld,
        ["verify_ports"] = string.Join(",", с.VerifyPorts),
        ["notify_emails"] = с.NotifyEmails.Length,
        ["enabled"] = с.Enabled,
    };

    private static Dictionary<string, object?> Разница(Dictionary<string, object?> было, Dictionary<string, object?> стало, bool пароль)
    {
        var разница = new Dictionary<string, object?>(StringComparer.Ordinal);

        foreach (var (ключ, значение) in стало)
        {
            if (!Equals(было.GetValueOrDefault(ключ), значение))
            {
                разница[ключ] = new[] { было.GetValueOrDefault(ключ), значение };
            }
        }

        if (пароль)
        {
            разница["password"] = "changed";
        }

        return разница;
    }

    /// <summary>Адрес Kerio, который не ведёт на сам сервис и служебные сети.</summary>
    private async Task<Uri> АдресAsync(string? текст, CancellationToken cancellationToken)
    {
        var uri = Адрес(текст);

        if (options.AllowLoopback)
        {
            return uri;
        }

        IPAddress[] адреса;

        try
        {
            адреса = IPAddress.TryParse(uri.Host.Trim('[', ']'), out var ip) ? [ip] : await Dns.GetHostAddressesAsync(uri.Host, cancellationToken);
        }
        catch (SocketException)
        {
            return uri;
        }

        return uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase) || адреса.Any(Служебный)
            ? throw Неверно("base_url", "Адрес ведёт на сам сервис или в служебную сеть (loopback, link-local): Kerio там быть не может.")
            : uri;
    }

    internal static bool Служебный(IPAddress ip)
    {
        if (ip.IsIPv4MappedToIPv6)
        {
            ip = ip.MapToIPv4();
        }

        if (IPAddress.IsLoopback(ip) || ip.IsIPv6LinkLocal || ip.IsIPv6Multicast || ip.Equals(IPAddress.Any) || ip.Equals(IPAddress.IPv6Any))
        {
            return true;
        }

        var b = ip.GetAddressBytes();
        return ip.AddressFamily == AddressFamily.InterNetwork && ((b[0] == 169 && b[1] == 254) || b[0] >= 224 || b[0] == 0);
    }

    private static Uri Адрес(string? текст)
    {
        if (string.IsNullOrWhiteSpace(текст))
        {
            throw Неверно("base_url", "Нужен адрес администрирования Kerio, например https://mail.example.com:4040.");
        }

        var строка = текст.Trim();

        if (!строка.Contains("://", StringComparison.Ordinal))
        {
            строка = "https://" + строка;
        }

        if (!Uri.TryCreate(строка, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps || string.IsNullOrEmpty(uri.Host))
        {
            throw Неверно("base_url", "Адрес Kerio — только https://: пароль администратора по открытому HTTP прочтёт любой на пути.");
        }

        if (!string.IsNullOrEmpty(uri.UserInfo) || uri.Query.Length > 0 || uri.Fragment.Length > 0)
        {
            throw Неверно("base_url", "Адрес — только схема, хост и порт, без логина, запроса и якоря.");
        }

        return new Uri($"https://{uri.Authority}/");
    }

    private static string Имя(string? текст)
    {
        var имя = текст?.Trim();

        return string.IsNullOrEmpty(имя) || имя.Length > KerioServer.MaxName
            ? throw Неверно("name", $"Имя сервера — от 1 до {KerioServer.MaxName} знаков.")
            : имя;
    }

    private static string Логин(string? текст)
    {
        var логин = текст?.Trim();

        return string.IsNullOrEmpty(логин) || логин.Length > 255
            ? throw Неверно("username", "Нужен логин учётной записи Kerio для сервиса.")
            : логин;
    }

    private static string Пароль(string? текст) =>
        string.IsNullOrEmpty(текст) || текст.Length > ПотолокПароля
            ? throw Неверно("password", "Нужен пароль учётной записи Kerio.")
            : текст;

    private static string Режим(string? текст) =>
        TlsModes.Known(текст)
            ? текст!
            : throw Неверно("tls_mode", "Доверие к сертификату Kerio: «ca» (центр сертификации) или «pin» (отпечаток).");

    private static string? Пин(string? режим, string? текст)
    {
        if (режим != TlsModes.Pin)
        {
            return null;
        }

        return Fingerprints.NormalizePin(текст)
            ?? throw Неверно("tls_pin_sha256", "Отпечаток SHA-256 — 64 шестнадцатеричных знака (разделители допустимы).");
    }

    private static string? ИмяTls(string? текст)
    {
        var имя = текст?.Trim().TrimEnd('.');

        if (string.IsNullOrEmpty(имя))
        {
            return null;
        }

        return Uri.CheckHostName(имя.Replace("*.", "x.", StringComparison.Ordinal)) == UriHostNameType.Dns && имя.Length <= 253
            ? имя.ToLowerInvariant()
            : throw Неверно("tls_server_name", "Имя в сертификате — DNS-имя, например mail.example.com.");
    }

    private static string? ИмяСертификата(string? текст)
    {
        var имя = текст?.Trim();

        return string.IsNullOrEmpty(имя)
            ? null
            : имя.Length > 255 ? throw Неверно("common_name", "Слишком длинное имя сертификата.") : имя;
    }

    private static int[] Порты(int[]? порты)
    {
        if (порты is null)
        {
            return [];
        }

        if (порты.Length > KerioServer.MaxVerifyPorts || порты.Any(п => п is < 1 or > 65535))
        {
            throw Неверно("verify_ports", $"Порты — числа от 1 до 65535, не больше {KerioServer.MaxVerifyPorts}.");
        }

        // ⚠ Проверка говорит TLS с первого байта.
        if (порты.FirstOrDefault(п => ПортыБезTls.Contains(п)) is var открытый and > 0)
        {
            throw Неверно(
                "verify_ports",
                $"Порт {открытый} работает без TLS или со STARTTLS — проверить на нём сертификат нельзя. "
                + "Укажите порты с TLS сразу: 443, 993, 995, 465, 636.");
        }

        return [.. порты.Distinct().Order()];
    }

    private static string[] Адреса(string[]? адреса)
    {
        if (адреса is null)
        {
            return [];
        }

        var годные = адреса.Select(а => а.Trim()).Where(а => а.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

        if (годные.Length > KerioServer.MaxNotifyEmails || годные.Any(а => !KerioNotifier.Годен(а)))
        {
            throw Неверно("notify_emails", $"Адреса — голые адреса почты, не больше {KerioServer.MaxNotifyEmails}.");
        }

        return годные;
    }

    private static AppException Неверно(string поле, string текст) => new(
        ErrorCodes.Validation,
        текст,
        HttpStatusCode.BadRequest,
        new Dictionary<string, object?>(StringComparer.Ordinal) { ["field"] = поле });

    private static AppException НетСервера() =>
        new(KerioErrorCodes.ServerNotFound, "Такого сервера нет.", HttpStatusCode.NotFound);

    private static AppException Занят() =>
        new(KerioErrorCodes.Busy, "Сервер сейчас проверяется или обновляется — повторите через минуту.", HttpStatusCode.Conflict);

    private static AppException Недоступен(string причина) => new(
        KerioErrorCodes.Unavailable,
        "Kerio не ответил или отказал.",
        HttpStatusCode.Conflict,
        new Dictionary<string, object?>(StringComparer.Ordinal) { ["reason"] = причина });
}
