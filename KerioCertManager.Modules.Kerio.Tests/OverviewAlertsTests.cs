using KerioCertManager.Emulators;
using KerioCertManager.Modules.Kerio.Api;
using KerioCertManager.Modules.Kerio.Certificates;
using KerioCertManager.Modules.Kerio.Client;
using KerioCertManager.Modules.Kerio.Domain;
using KerioCertManager.Modules.Kerio.Notifications;
using KerioCertManager.Modules.Kerio.Sync;
using KerioCertManager.Modules.KeyManager.Contracts;

namespace KerioCertManager.Modules.Kerio.Tests;

/// <summary>Обзор хранилища Kerio и правила оповещений: пороги, «не обновился сам», происхождение сертификатов.</summary>
public sealed class OverviewAlertsTests
{
    private static readonly DateTimeOffset Сейчас = new(2026, 10, 30, 12, 0, 0, TimeSpan.Zero);

    private const string Имя = "mail.example.com";

    [Theory]
    [InlineData("7, 3, 1", new[] { 7, 3, 1 })]
    [InlineData("1;7 3,x,0,400,7", new[] { 7, 3, 1 })]
    [InlineData("", new int[0])]
    [InlineData(null, new int[0])]
    public void Дни_напоминаний_разбираются_по_убыванию_без_мусора(string? текст, int[] ждём) =>
        KerioNotifier.Напоминания(текст).ShouldBe(ждём);

    [Theory]
    [InlineData(10.0, null)]
    [InlineData(7.0, 7)]
    [InlineData(6.2, 7)]
    [InlineData(2.5, 3)]
    [InlineData(0.4, 1)]
    public void Ступень_напоминания_наименьший_пройденный_порог(double дней, int? ждём) =>
        KerioNotifier.Ступень(Сейчас.AddDays(дней), [7, 3, 1], Сейчас).ShouldBe(ждём);

    [Fact]
    public void Без_напоминаний_ступени_нет() =>
        KerioNotifier.Ступень(Сейчас.AddDays(1), [], Сейчас).ShouldBeNull();

    [Fact]
    public void Продление_ждёт_дольше_срока_пишется()
    {
        var сервер = Сервер(installed: "aa", served: "aa");
        var план = DeployPlanner.Plan(сервер, Подан("aa", 60), Выпуск("bb", 90, замечен: -30), false, 21, Сейчас);

        план.Behind.ShouldBeTrue();

        var тревога = SyncCycle.Просрочка(сервер, план, 24, 21, Сейчас).ShouldNotBeNull();
        тревога.Kind.ShouldBe(AlertKinds.Overdue);
        тревога.Level.ShouldBe(AlertLevel.Warning);
        тревога.Message.ShouldContain("Автоматическая установка");
    }

    [Fact]
    public void Продление_моложе_срока_не_пишется()
    {
        var сервер = Сервер(installed: "aa", served: "aa");
        var план = DeployPlanner.Plan(сервер, Подан("aa", 60), Выпуск("bb", 90, замечен: -10), false, 21, Сейчас);

        SyncCycle.Просрочка(сервер, план, 24, 21, Сейчас).ShouldBeNull();
    }

    /// <summary>Сервер подключили вчера к давнему выпуску — отсчёт с подключения, а не с месячной давности продления.</summary>
    [Fact]
    public void Отсчёт_не_раньше_подключения_сервера()
    {
        var сервер = Сервер(installed: "aa", served: "aa");
        сервер.CreatedAt = Сейчас.AddHours(-5);
        var план = DeployPlanner.Plan(сервер, Подан("aa", 60), Выпуск("bb", 90, замечен: -24 * 30), false, 21, Сейчас);

        SyncCycle.Просрочка(сервер, план, 24, 21, Сейчас).ShouldBeNull();
    }

    [Fact]
    public void Истекающий_и_не_обновлённый_критичен()
    {
        var сервер = Сервер(installed: "aa", served: "aa", servedDays: 5);
        сервер.LastDeployError = "Kerio не принял ключ.";
        var план = DeployPlanner.Plan(сервер, Подан("aa", 5), Выпуск("bb", 90, замечен: -48), false, 21, Сейчас);

        var тревога = SyncCycle.Просрочка(сервер, план, 24, 21, Сейчас).ShouldNotBeNull();
        тревога.Level.ShouldBe(AlertLevel.Critical);
        тревога.Message.ShouldContain("Kerio не принял ключ.");
    }

    [Fact]
    public void Не_отстаёт_когда_стоит_текущий_правка_вручную_или_замена_не_нужна()
    {
        DeployPlanner.Plan(Сервер(installed: "bb", served: "bb"), Подан("bb", 90), Выпуск("bb", 90), true, 21, Сейчас).Behind.ShouldBeFalse();

        DeployPlanner.Plan(Сервер(installed: "bb", served: "aa"), Подан("aa", 300), Выпуск("bb", 90), true, 21, Сейчас).Behind.ShouldBeFalse();

        var дольше = Подан("aa", 300) with { Subject = Имя, DnsNames = [Имя] };
        DeployPlanner.Plan(Сервер(installed: null, served: "aa"), дольше, Выпуск("bb", 90), true, 21, Сейчас).Behind.ShouldBeFalse();
    }

    [Fact]
    public void Установка_и_повтор_отстают()
    {
        DeployPlanner.Plan(Сервер(installed: "aa", served: "aa"), Подан("aa", 60), Выпуск("bb", 90), true, 21, Сейчас).Behind.ShouldBeTrue();

        var ждёт = Сервер(installed: "aa", served: "aa");
        ждёт.FailedSerial = "bb";
        ждёт.DeployFailures = 2;
        ждёт.NextAttemptAt = Сейчас.AddMinutes(10);
        DeployPlanner.Plan(ждёт, Подан("aa", 60), Выпуск("bb", 90), true, 21, Сейчас).Behind.ShouldBeTrue();
    }

    [Theory]
    [InlineData(1, 1, true)]
    [InlineData(2, 3, false)]
    [InlineData(3, 3, true)]
    public void Недоступен_со_скольких_проверок(int подряд, int порог, bool пишем)
    {
        var сервер = Сервер(installed: null, served: null);
        сервер.CheckFailures = подряд;
        var проверка = new InspectionResult(null, "соединение отклонено", false, null, null, null, null, null);

        SyncCycle.Связь(сервер, проверка, порог).Any(а => а.Kind == AlertKinds.Unreachable).ShouldBe(пишем);
    }

    [Fact]
    public void Происхождение_и_срок_каждого_сертификата_хранилища()
    {
        var сервер = Сервер(installed: "aa", served: "aa");
        сервер.InstalledSha256 = "SHA-AA";
        сервер.ServedSha256 = "SHA-AA";
        сервер.Store =
        [
            Лежит("server1", "aa", "SHA-AA", 60),
            Лежит("server2", "cc", "SHA-CC", 10, subject: "vpn.example.com"),
            Лежит("server3", "dd", "SHA-DD", -3, subject: "legacy.example.com"),
            Лежит("server", null, "SHA-K", 400, subject: "kerio", selfSigned: true),
            Лежит("server4", "bb", "SHA-BB", 90),
        ];

        IReadOnlyList<SourceCertificate> kmp = [Выпуск("bb", 90), new("vpn.example.com", "0CC", null, Сейчас, Сейчас)];

        var список = ServerView.Хранилище(сервер, kmp, 21, Сейчас);

        список[0].Id.ShouldBe("server1");
        список[0].Served.ShouldBeTrue();

        var по = список.ToDictionary(з => з.Id);
        по["server1"].Origin.ShouldBe("managed");
        по["server4"].Origin.ShouldBe("managed");
        по["server2"].Origin.ShouldBe("kmp");
        по["server2"].KmpCommonName.ShouldBe("vpn.example.com");
        по["server2"].State.ShouldBe("expiring");
        по["server3"].Origin.ShouldBe("foreign");
        по["server3"].State.ShouldBe("expired");
        по["server"].Origin.ShouldBe("builtin");
        по["server1"].State.ShouldBe("ok");
        список.Count(з => з.Served).ShouldBe(1);
    }

    /// <summary>⚠ «Обновить» — только у выбранного для сервера имени, когда в KMP есть выпуск новее, ещё не лежащий в Kerio.</summary>
    [Fact]
    public void Обновить_только_выбранное_и_когда_в_KMP_новее()
    {
        var сервер = Сервер(installed: "aa", served: "aa");
        сервер.Store =
        [
            Лежит("server1", "aa", "SHA-AA", 60),
            Лежит("server2", "dd", "SHA-DD", -117, subject: "legacy.example.com"),
            Лежит("server", null, "SHA-K", 400, subject: "kerio", selfSigned: true),
        ];
        var свежий = new SourceCertificate("legacy.example.com", "ee", DateOnly.FromDateTime(Сейчас.AddDays(300).UtcDateTime), Сейчас, Сейчас);
        IReadOnlyList<SourceCertificate> kmp = [Выпуск("aa", 60), свежий];

        ServerView.Хранилище(сервер, kmp, 21, Сейчас).ShouldAllBe(з => з.UpdateFrom == null, "legacy не выбран для сервера — не трогаем");

        сервер.Extras = [new ExtraCertificate { CommonName = "legacy.example.com", AddedAt = Сейчас }];
        var по = ServerView.Хранилище(сервер, kmp, 21, Сейчас).ToDictionary(з => з.Id);
        по["server2"].UpdateFrom.ShouldBe("legacy.example.com", "выбран, в KMP новее");
        по["server1"].UpdateFrom.ShouldBeNull("текущий выпуск KMP уже лежит");
        по["server"].UpdateFrom.ShouldBeNull();

        сервер.Store = [.. сервер.Store, Лежит("server3", "ee", "SHA-EE", 300, subject: "legacy.example.com")];
        ServerView.Хранилище(сервер, kmp, 21, Сейчас).ShouldAllBe(з => з.UpdateFrom == null, "новый уже в хранилище");
    }

    /// <summary>⚠ Версия имени раньше самой поздней, которую Kerio не подаёт, — заменена: в сводку не идёт, кнопки нет, в конце списка.</summary>
    [Fact]
    public void Старая_версия_рядом_с_новой_заменена()
    {
        var сервер = Сервер(installed: "aa", served: "aa");
        сервер.ServedSha256 = "SHA-AA";
        сервер.ServedByName = new(StringComparer.Ordinal) { ["legacy.example.com"] = "SHA-OLD2" };
        сервер.Extras = [new ExtraCertificate { CommonName = "legacy.example.com", AddedAt = Сейчас }];
        сервер.Store =
        [
            Лежит("server1", "aa", "SHA-AA", 60),
            Лежит("server2", "a0", "SHA-OLD", -117),
            Лежит("server3", "dd", "SHA-OLD2", -50, subject: "legacy.example.com"),
            Лежит("server4", "ee", "SHA-NEW", 300, subject: "legacy.example.com"),
            Лежит("server5", "ff", "SHA-ALONE", -10, subject: "alone.example.com"),
        ];
        IReadOnlyList<SourceCertificate> kmp =
            [Выпуск("aa", 60), new("legacy.example.com", "f1", DateOnly.FromDateTime(Сейчас.AddDays(400).UtcDateTime), Сейчас, Сейчас)];

        var хранилище = ServerView.Хранилище(сервер, kmp, 21, Сейчас);
        var по = хранилище.ToDictionary(з => з.Id);

        по["server2"].Superseded.ShouldBeTrue("рядом новее, и Kerio её не подаёт");
        по["server2"].State.ShouldBe("expired", "срок показан честно, но в сводку не идёт");
        по["server1"].Superseded.ShouldBeFalse();
        по["server3"].Superseded.ShouldBeFalse("Kerio подаёт её для имени — клиенты её видят");
        по["server3"].UpdateFrom.ShouldBe("legacy.example.com");
        по["server4"].Superseded.ShouldBeFalse();
        по["server5"].Superseded.ShouldBeFalse("истёк, но новее нет");
        хранилище[^1].Id.ShouldBe("server2", "заменённые — в конце");

        сервер.ServedByName = new(StringComparer.Ordinal);
        по = ServerView.Хранилище(сервер, kmp, 21, Сейчас).ToDictionary(з => з.Id);
        по["server3"].Superseded.ShouldBeTrue("больше не подаётся");
        по["server3"].UpdateFrom.ShouldBeNull("у заменённой кнопки нет");
        по["server4"].UpdateFrom.ShouldBe("legacy.example.com", "кнопка — у новейшей версии");
    }

    /// <summary>⚠ Тот же идентификатор, имя и дата — ещё не тот же сертификат: текст читается каждый раз.</summary>
    [Fact]
    public async Task Текст_сертификата_читается_каждый_раз()
    {
        using var первый = TestPki.SelfSigned("mail.example.com");
        using var перевыпуск = TestPki.SelfSigned("mail.example.com");

        var до = new DateOnly(первый.NotAfter.Year, первый.NotAfter.Month, первый.NotAfter.Day);
        IReadOnlyList<KerioCertificate> список =
        [
            new("server1", KerioCertificate.Active, "mail.example.com", "mail.example.com", до, "Valid", true, false, []),
            new("req1", "CertificateRequest", "csr.example.com", string.Empty, null, string.Empty, false, false, []),
        ];

        var сессия = new Сессия(new Dictionary<string, string> { ["server1"] = первый.ExportCertificatePem() });
        var итог = await ServerInspection.ХранилищеAsync(сессия, список, CancellationToken.None);

        итог[0].Sha256.ShouldBe(Fingerprints.Sha256(первый));
        итог[0].Serial.ShouldBe(Fingerprints.Serial(первый.SerialNumber));
        итог[1].Sha256.ShouldBeNull("запрос — не сертификат, текста у него не спрашиваем");

        var снова = new Сессия(new Dictionary<string, string> { ["server1"] = перевыпуск.ExportCertificatePem() });
        итог = await ServerInspection.ХранилищеAsync(снова, список, CancellationToken.None);

        снова.Прочитано.ShouldBe(["server1"]);
        итог[0].Sha256.ShouldBe(Fingerprints.Sha256(перевыпуск));
    }

    [Fact]
    public async Task Сбой_чтения_одного_не_валит_хранилище()
    {
        var сессия = new Сессия(new Dictionary<string, string>());
        IReadOnlyList<KerioCertificate> список =
            [new("server1", KerioCertificate.Active, "a.example.com", "CA", new DateOnly(2027, 1, 1), "Valid", false, false, [])];

        var итог = await ServerInspection.ХранилищеAsync(сессия, список, CancellationToken.None);

        итог.ShouldHaveSingleItem().Sha256.ShouldBeNull();
        итог[0].NotAfter.ShouldBe(new DateTimeOffset(2027, 1, 1, 0, 0, 0, TimeSpan.Zero));
    }

    private static KerioServer Сервер(string? installed, string? served, int servedDays = 60) => new()
    {
        Id = Guid.CreateVersion7(),
        Name = "kerio",
        BaseUrl = "https://kerio.example.com:4040",
        Username = "kcm",
        TlsMode = TlsModes.Pin,
        CommonName = Имя,
        InstalledSerial = installed,
        ServedSerial = served,
        ServedNotAfter = served is null ? null : Сейчас.AddDays(servedDays),
        AutoDeploy = true,
        CreatedAt = Сейчас.AddDays(-365),
    };

    private static ServedCertificate Подан(string serial, int days) => new(
        "SHA" + serial, Имя, "Issuing CA", serial, Сейчас.AddDays(days - 90), Сейчас.AddDays(days), [Имя], false, true, string.Empty);

    private static SourceCertificate Выпуск(string serial, int days, int замечен = -1) =>
        new(Имя, serial, DateOnly.FromDateTime(Сейчас.AddDays(days).UtcDateTime), Сейчас.AddHours(замечен), Сейчас);

    private static StoredCertificate Лежит(string id, string? serial, string sha, int days, string subject = Имя, bool selfSigned = false) =>
        new(id, KerioCertificate.Inactive, subject, selfSigned ? subject : "Issuing CA", Сейчас.AddDays(days - 365), Сейчас.AddDays(days),
            serial, sha, [subject], selfSigned, false);

    /// <summary>Сессия, которая только отдаёт тексты сертификатов и считает, чьи просили.</summary>
    private sealed class Сессия(Dictionary<string, string> тексты) : IKerioSession
    {
        public List<string> Прочитано { get; } = [];

        public Task<string> SourceAsync(string id, CancellationToken cancellationToken = default)
        {
            Прочитано.Add(id);
            return тексты.TryGetValue(id, out var текст)
                ? Task.FromResult(текст)
                : throw new KerioException(KerioFailure.Rejected, "Kerio не отдал текст.");
        }

        public Task<KerioIdentity> WhoAmIAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<string?> VersionAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<IReadOnlyList<KerioCertificate>> CertificatesAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<string> UploadAsync(byte[] content, string fileName, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<KerioKeyImport> ImportPrivateKeyAsync(string fileId, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task ImportCertificateAsync(string keyId, string fileId, string password, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task SetActiveAsync(string id, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<IReadOnlyList<string>> RemoveAsync(IReadOnlyList<string> ids, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
