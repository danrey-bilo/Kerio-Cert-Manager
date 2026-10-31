using KerioCertManager.Emulators;
using KerioCertManager.Modules.Kerio.Certificates;
using KerioCertManager.Modules.Kerio.Client;
using KerioCertManager.Modules.Kerio.Domain;
using KerioCertManager.Modules.Kerio.Sync;
using KerioCertManager.Modules.KeyManager.Contracts;
using Microsoft.Extensions.Logging.Abstractions;

namespace KerioCertManager.Modules.Kerio.Tests;

/// <summary>Установка на эмулятор Kerio 10.0.6 по HTTPS: удача, залипание, откат, отказы, пробный прогон, уборка.</summary>
public sealed class DeployerTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly TimeSpan Проверка = TimeSpan.FromSeconds(8);

    [Fact]
    public async Task Продление_ставится_и_подтверждается_на_всех_портах()
    {
        await using var стенд = await Stand.StartAsync();
        var сервер = стенд.Сервер(стенд.A, Stand.Почта);
        var прежний = стенд.A.ConfigActiveId;
        var новый = стенд.Host.Kmp.Issue(Stand.Почта, TimeSpan.FromDays(90));
        var шаги = new List<DeployStep>();

        var итог = await Установить(стенд, сервер, новый, шаги);

        итог.Outcome.ShouldBe(DeployOutcomes.Succeeded, итог.Error);
        итог.Sha256.ShouldBe(Fingerprints.Sha256(новый.Certificate));
        итог.PreviousKerioId.ShouldBe(прежний);
        итог.KerioId.ShouldBe(стенд.A.ConfigActiveId);
        стенд.A.ServedCertificate.SerialNumber.ShouldBe(новый.Certificate.SerialNumber);
        стенд.A.Store.ShouldContain(з => з.Id == прежний, "прежний — единственный путь назад, его не трогаем");

        шаги.ShouldAllBe(ш => ш.Ok);
        шаги.Select(ш => ш.Step).ShouldContain(DeploySteps.Verify);
        шаги.Last().Step.ShouldBe(DeploySteps.Done);
    }

    /// <summary>⚠ Kerio принял, но порты не переключились: клиенты ничего не заметили, прежний — активный и в настройках.</summary>
    [Fact]
    public async Task Залипание_Kerio_прежний_остаётся_и_в_настройках()
    {
        await using var стенд = await Stand.StartAsync();
        var сервер = стенд.Сервер(стенд.A, Stand.Почта);
        var прежний = стенд.A.ConfigActiveId;
        стенд.A.Stuck = true;

        var итог = await Установить(стенд, сервер, стенд.Host.Kmp.Issue(Stand.Почта, TimeSpan.FromDays(90)));

        итог.Outcome.ShouldBe(DeployOutcomes.Failed);
        итог.FailedStep.ShouldBe(DeploySteps.Verify);
        итог.Error.ShouldNotBeNull().ShouldContain("не начал подавать");
        стенд.A.ServedId.ShouldBe(прежний);
        стенд.A.ConfigActiveId.ShouldBe(прежний, "прежний снова активный в настройках Kerio — после перезапуска не подхватит новый");
    }

    /// <summary>⚠ Как живой 10.0.6: после импорта Kerio перезапускает HTTPS и обрывает все сессии API.</summary>
    [Fact]
    public async Task Откат_после_обрыва_сессии_входит_заново()
    {
        await using var стенд = await Stand.StartAsync();
        var сервер = стенд.Сервер(стенд.A, Stand.Почта);
        var прежний = стенд.A.ConfigActiveId;
        стенд.A.Stuck = true;
        стенд.A.SessionResetAfterChange = TimeSpan.FromSeconds(1);

        var итог = await Установить(стенд, сервер, стенд.Host.Kmp.Issue(Stand.Почта, TimeSpan.FromDays(90)));

        итог.FailedStep.ShouldBe(DeploySteps.Verify);
        итог.RollbackConfirmed.ShouldBeTrue(итог.Error);
        стенд.A.SessionResets.ShouldBeGreaterThan(0, "сессия обрывалась посреди установки");
        стенд.A.ConfigActiveId.ShouldBe(прежний, "откат дошёл до Kerio новой сессией");
    }

    /// <summary>Новый встал на порту администрирования, но не на порту службы — возвращаем прежний и подтверждаем.</summary>
    [Fact]
    public async Task Порт_службы_не_переключился_откат_с_подтверждением()
    {
        await using var стенд = await Stand.StartAsync();
        var сервер = стенд.Сервер(стенд.A, Stand.Почта);
        var прежний = стенд.A.ConfigActiveId;
        var прежнийSha = Fingerprints.Sha256(стенд.A.ServedCertificate);
        var tls = new ЗалипшийПорт(стенд.Tls, стенд.A.ServicePorts[0], прежнийSha);
        var выпуск = стенд.Host.Kmp.Issue(Stand.Почта, TimeSpan.FromDays(90));

        var итог = await Установить(стенд, сервер, выпуск, tls: tls);

        итог.Outcome.ShouldBe(DeployOutcomes.RolledBack, итог.Error);
        итог.RollbackConfirmed.ShouldBeTrue();
        стенд.A.ServedId.ShouldBe(прежний);
        стенд.A.ConfigActiveId.ShouldBe(прежний);
        стенд.A.Store.ShouldNotContain(
            з => з.Certificate.SerialNumber == выпуск.Certificate.SerialNumber,
            "не вставший убран: он истекает позже, и по имени Kerio подавал бы его и после отката");
    }

    /// <summary>⚠ Остановка сервиса посреди проверки — после активации: прежний возвращается, а не бросается на полпути.</summary>
    [Fact]
    public async Task Остановка_после_активации_возвращает_прежний()
    {
        await using var стенд = await Stand.StartAsync();
        var сервер = стенд.Сервер(стенд.A, Stand.Почта);
        var прежний = стенд.A.ConfigActiveId;
        var прежнийSha = Fingerprints.Sha256(стенд.A.ServedCertificate);
        using var остановка = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var tls = new ОстановкаНаПроверке(new ЗалипшийПорт(стенд.Tls, стенд.A.ServicePorts[0], прежнийSha), остановка, прежнийSha);
        var шаги = new List<DeployStep>();

        var итог = await Установить(стенд, сервер, стенд.Host.Kmp.Issue(Stand.Почта, TimeSpan.FromDays(90)), шаги, tls: tls, token: остановка.Token);

        остановка.IsCancellationRequested.ShouldBeTrue("отмена пришла посреди проверки");
        итог.Outcome.ShouldBe(DeployOutcomes.RolledBack, итог.Error);
        итог.Error.ShouldNotBeNull().ShouldContain("остановкой сервиса");
        стенд.A.ServedId.ShouldBe(прежний);
        стенд.A.ConfigActiveId.ShouldBe(прежний);
        шаги.ShouldContain(ш => ш.Step == DeploySteps.Rollback && ш.Ok);
    }

    /// <summary>Остановка до активации — просто выход: Kerio не тронут.</summary>
    [Fact]
    public async Task Остановка_до_активации_не_трогает_Kerio()
    {
        await using var стенд = await Stand.StartAsync();
        var было = стенд.A.Store.Count;
        using var остановка = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        await остановка.CancelAsync();

        await Should.ThrowAsync<OperationCanceledException>(() => Установить(
            стенд, стенд.Сервер(стенд.A, Stand.Почта), стенд.Host.Kmp.Issue(Stand.Почта, TimeSpan.FromDays(90)), token: остановка.Token));

        стенд.A.Store.Count.ShouldBe(было);
    }

    /// <summary>⚠ Журнал установки не пишется (база мигнула) — откат всё равно идёт до конца.</summary>
    [Fact]
    public async Task Сбой_журнала_не_мешает_откату()
    {
        await using var стенд = await Stand.StartAsync();
        var сервер = стенд.Сервер(стенд.A, Stand.Почта);
        var прежний = стенд.A.ConfigActiveId;
        var прежнийSha = Fingerprints.Sha256(стенд.A.ServedCertificate);
        var tls = new ЗалипшийПорт(стенд.Tls, стенд.A.ServicePorts[0], прежнийSha);

        var итог = await Установить(
            стенд, сервер, стенд.Host.Kmp.Issue(Stand.Почта, TimeSpan.FromDays(90)), tls: tls, журналЛомается: true);

        итог.Outcome.ShouldBe(DeployOutcomes.RolledBack, итог.Error);
        стенд.A.ServedId.ShouldBe(прежний);
        стенд.A.ConfigActiveId.ShouldBe(прежний);
    }

    /// <summary>⚠ Подаётся сертификат, которого нет в хранилище: возвращаться некуда — сам сервис не ставит, человек может.</summary>
    [Fact]
    public async Task Без_пути_назад_автоустановка_отказывает_до_загрузки()
    {
        await using var стенд = await Stand.StartAsync();
        var сервер = стенд.Сервер(стенд.A, Stand.Почта);
        var было = стенд.A.Store.Count;
        var выпуск = стенд.Host.Kmp.Issue(Stand.Почта, TimeSpan.FromDays(90));

        var неизвестный = new НеизвестныйНаПорту(стенд.Tls, стенд.A.AdminPort);

        var сам = await Установить(стенд, сервер, выпуск, tls: неизвестный, безПутиНазад: true);

        сам.Outcome.ShouldBe(DeployOutcomes.Failed);
        сам.FailedStep.ShouldBe(DeploySteps.Inventory);
        стенд.A.Store.Count.ShouldBe(было, "ничего не загружено");

        var человек = await Установить(стенд, сервер, выпуск, manual: true);
        человек.Outcome.ShouldBe(DeployOutcomes.Succeeded, человек.Error);
    }

    [Fact]
    public async Task Отказ_импорта_Kerio_не_изменён()
    {
        await using var стенд = await Stand.StartAsync();
        var сервер = стенд.Сервер(стенд.A, Stand.Почта);
        var прежний = стенд.A.ServedId;
        var было = стенд.A.Store.Count;
        стенд.A.FailingMethods.Add("ConnectCertificate.importCertificate");

        var итог = await Установить(стенд, сервер, стенд.Host.Kmp.Issue(Stand.Почта, TimeSpan.FromDays(90)));

        итог.Outcome.ShouldBe(DeployOutcomes.Failed);
        итог.Error.ShouldNotBeNull().ShouldContain("importCertificate");
        стенд.A.Store.Count.ShouldBe(было);
        стенд.A.ServedId.ShouldBe(прежний);
    }

    [Fact]
    public async Task Ключ_не_от_того_сертификата_Kerio_даже_не_трогаем()
    {
        await using var стенд = await Stand.StartAsync();
        var сервер = стенд.Сервер(стенд.A, Stand.Почта);
        стенд.Источник.ЧужойКлюч = true;

        var итог = await Установить(стенд, сервер, стенд.Host.Kmp.Issue(Stand.Почта, TimeSpan.FromDays(90)));

        итог.Outcome.ShouldBe(DeployOutcomes.Failed);
        итог.FailedStep.ShouldBe(DeploySteps.Validate);
        стенд.A.Logins.ShouldBe(0);
    }

    [Fact]
    public async Task Отказ_KMP_до_входа_в_Kerio()
    {
        await using var стенд = await Stand.StartAsync();
        стенд.Источник.Отказ = "Key Manager Plus недоступен";

        var итог = await Установить(стенд, стенд.Сервер(стенд.A, Stand.Почта), стенд.Host.Kmp.Issue(Stand.Почта, TimeSpan.FromDays(90)));

        итог.FailedStep.ShouldBe(DeploySteps.Fetch);
        стенд.A.Logins.ShouldBe(0);
    }

    [Fact]
    public async Task Пробный_прогон_ничего_не_меняет()
    {
        await using var стенд = await Stand.StartAsync();
        var было = стенд.A.Store.Count;
        var подаётся = стенд.A.ServedId;

        var итог = await Установить(
            стенд, стенд.Сервер(стенд.A, Stand.Почта), стенд.Host.Kmp.Issue(Stand.Почта, TimeSpan.FromDays(90)), пробно: true);

        итог.Outcome.ShouldBe(DeployOutcomes.DryRun);
        стенд.A.Store.Count.ShouldBe(было);
        стенд.A.ServedId.ShouldBe(подаётся);
        стенд.A.Logins.ShouldBe(1, "вход и чтение хранилища проверены");
    }

    /// <summary>Сертификат уже в хранилище (вернули прежний вручную) — повторный импорт не нужен, только активация.</summary>
    [Fact]
    public async Task Повторная_установка_без_повторного_импорта()
    {
        await using var стенд = await Stand.StartAsync();
        var сервер = стенд.Сервер(стенд.A, Stand.Почта);
        var прежний = стенд.A.ConfigActiveId;
        var новый = стенд.Host.Kmp.Issue(Stand.Почта, TimeSpan.FromDays(90));

        (await Установить(стенд, сервер, новый)).Outcome.ShouldBe(DeployOutcomes.Succeeded);

        сервер.TlsPinSha256 = Fingerprints.Sha256(стенд.A.ServedCertificate);
        await using (var сессия = await стенд.Соединитель.OpenAsync(сервер, Ct))
        {
            await сессия.SetActiveAsync(прежний, Ct);
        }

        сервер.TlsPinSha256 = Fingerprints.Sha256(стенд.A.ServedCertificate);

        var было = стенд.A.Store.Count;
        var шаги = new List<DeployStep>();

        var итог = await Установить(стенд, сервер, новый, шаги, manual: true);

        итог.Outcome.ShouldBe(DeployOutcomes.Succeeded, итог.Error);
        стенд.A.Store.Count.ShouldBe(было);
        шаги.ShouldContain(ш => ш.Message.Contains("повторный импорт не нужен"));
        стенд.A.ServedCertificate.SerialNumber.ShouldBe(новый.Certificate.SerialNumber);
    }

    [Fact]
    public async Task Уборка_оставляет_предыдущий_и_встроенный()
    {
        await using var стенд = await Stand.StartAsync();
        var сервер = стенд.Сервер(стенд.A, Stand.Почта);
        сервер.CleanupOld = true;
        var первый = стенд.A.ConfigActiveId;

        var второй = await Установить(стенд, сервер, стенд.Host.Kmp.Issue(Stand.Почта, TimeSpan.FromDays(90)));
        сервер.TlsPinSha256 = второй.Sha256;
        var третий = await Установить(стенд, сервер, стенд.Host.Kmp.Issue(Stand.Почта, TimeSpan.FromDays(90)));

        третий.Outcome.ShouldBe(DeployOutcomes.Succeeded, третий.Error);
        стенд.A.Store.Select(з => з.Id).ShouldBe(["server", второй.KerioId!, третий.KerioId!], ignoreOrder: true);
        стенд.A.Store.ShouldNotContain(з => з.Id == первый);
    }

    /// <summary>⚠ Уборка — только своих: купленный или положенный вручную того же имени остаётся (ключа от него может больше нигде не быть), а свои…</summary>
    [Fact]
    public async Task Уборка_не_трогает_чужие_того_же_имени()
    {
        await using var стенд = await Stand.StartAsync();
        var сервер = стенд.Сервер(стенд.A, Stand.Почта);
        сервер.CleanupOld = true;
        var исходный = стенд.A.ConfigActiveId;

        var чужой = стенд.Host.Pki.Issue([Stand.Почта], DateTimeOffset.UtcNow.AddDays(-10), DateTimeOffset.UtcNow.AddDays(60));
        var чужойId = стенд.A.Keep(чужой, чужой.ExportCertificatePem() + "\n" + стенд.Host.Pki.Intermediate.ExportCertificatePem());

        async Task<DeployResult> Поставить(KmpEntry выпуск, List<DeployStep>? шаги = null)
        {
            var итог = await Установить(стенд, сервер, выпуск, шаги);
            итог.Outcome.ShouldBe(DeployOutcomes.Succeeded, итог.Error);
            сервер.TlsPinSha256 = итог.Sha256;
            сервер.PreviousSerials = InstalledHistory.Push(сервер.InstalledSerial, сервер.PreviousSerials, выпуск.Serial);
            сервер.InstalledSerial = выпуск.Serial;

            return итог;
        }

        var первый = стенд.Host.Kmp.Issue(Stand.Почта, TimeSpan.FromDays(90));
        var перваяУстановка = await Поставить(первый);
        var второй = await Поставить(стенд.Host.Kmp.Issue(Stand.Почта, TimeSpan.FromDays(90)));

        стенд.A.Store.ShouldNotContain(з => з.Id == исходный, "исходный — выпуск KMP, свой");
        стенд.A.Store.ShouldContain(з => з.Id == чужойId);

        стенд.Host.Kmp.Remove(первый.Serial);
        var шаги = new List<DeployStep>();
        var третий = await Поставить(стенд.Host.Kmp.Issue(Stand.Почта, TimeSpan.FromDays(90)), шаги);

        стенд.A.Store.Select(з => з.Id).ShouldBe(["server", чужойId, второй.KerioId!, третий.KerioId!], ignoreOrder: true);
        стенд.A.Store.ShouldNotContain(з => з.Id == перваяУстановка.KerioId);
        шаги.ShouldContain(ш => ш.Step == DeploySteps.Cleanup && ш.Message.Contains("не трогаются") && ш.Message.Contains(чужойId));
    }

    [Fact]
    public async Task Роль_без_прав_видна_при_проверке()
    {
        await using var стенд = await Stand.StartAsync();
        стенд.A.Role = "Auditor";

        var проверка = await new ServerInspection(стенд.Соединитель, стенд.Tls)
            .InspectAsync(стенд.Сервер(стенд.A, Stand.Почта), forceApi: false, Ct);

        проверка.Served.ShouldNotBeNull();
        проверка.ApiFailure.ShouldBe(KerioFailure.Forbidden);
        проверка.Error.ShouldNotBeNull().ShouldContain("полного администратора");
    }

    [Fact]
    public async Task Отвергнутый_пароль_не_повторяется_без_нажатия()
    {
        await using var стенд = await Stand.StartAsync();
        var сервер = стенд.Сервер(стенд.A, Stand.Почта, password: "неверный");
        var проверка = new ServerInspection(стенд.Соединитель, стенд.Tls);

        var первая = await проверка.InspectAsync(сервер, forceApi: false, Ct);
        ServerInspection.Apply(сервер, первая, DateTimeOffset.UtcNow);
        сервер.AuthRejected.ShouldBeTrue();

        await проверка.InspectAsync(сервер, forceApi: false, Ct);
        стенд.A.FailedLogins.ShouldBe(1, "второй проход не входит — иначе Kerio заблокирует учётную запись");

        await проверка.InspectAsync(сервер, forceApi: true, Ct);
        стенд.A.FailedLogins.ShouldBe(2, "«Проверить» входит всегда");
    }

    private static Task<DeployResult> Установить(
        Stand стенд,
        KerioServer сервер,
        KmpEntry выпуск,
        List<DeployStep>? шаги = null,
        bool manual = false,
        bool пробно = false,
        ITlsInspector? tls = null,
        CancellationToken? token = null,
        bool журналЛомается = false,
        bool безПутиНазад = false)
    {
        var deployer = new KerioDeployer(стенд.Источник, стенд.Соединитель, tls ?? стенд.Tls, TimeProvider.System, NullLogger<KerioDeployer>.Instance);

        return deployer.DeployAsync(
            new DeployRequest(
                сервер,
                new SourceCertificate(выпуск.CommonName, выпуск.Serial, null, выпуск.CreatedAt, DateTimeOffset.UtcNow),
                manual,
                пробно,
                Проверка,
                RequireRollbackTarget: безПутиНазад),
            шаг =>
            {
                шаги?.Add(шаг);
                return журналЛомается ? throw new InvalidOperationException("база недоступна") : Task.CompletedTask;
            },
            token ?? Ct);
    }

    /// <summary>Отмена (остановка сервиса), как только проверка увидела новый на порту администрирования.</summary>
    private sealed class ОстановкаНаПроверке(ITlsInspector настоящий, CancellationTokenSource остановка, string прежнийSha) : ITlsInspector
    {
        public async Task<ServedCertificate> InspectAsync(string host, int port, CancellationToken cancellationToken = default)
        {
            var подан = await настоящий.InspectAsync(host, port, cancellationToken);

            if (подан.Sha256 != прежнийSha && !остановка.IsCancellationRequested)
            {
                await остановка.CancelAsync();
            }

            return подан;
        }
    }

    /// <summary>На порту подаётся сертификат, которого нет в хранилище Kerio.</summary>
    private sealed class НеизвестныйНаПорту(ITlsInspector настоящий, int порт) : ITlsInspector
    {
        public async Task<ServedCertificate> InspectAsync(string host, int port, CancellationToken cancellationToken = default)
        {
            var подан = await настоящий.InspectAsync(host, port, cancellationToken);

            return port == порт ? подан with { Sha256 = new string('F', 64), Subject = "неизвестный" } : подан;
        }
    }

    /// <summary>Порт службы «застрял» на прежнем сертификате, пока активен новый.</summary>
    private sealed class ЗалипшийПорт(ITlsInspector настоящий, int порт, string прежнийSha) : ITlsInspector
    {
        public async Task<ServedCertificate> InspectAsync(string host, int port, CancellationToken cancellationToken = default)
        {
            var подан = await настоящий.InspectAsync(host, port, cancellationToken);

            return port == порт && подан.Sha256 != прежнийSha ? подан with { Sha256 = прежнийSha, Subject = "прежний" } : подан;
        }
    }
}
