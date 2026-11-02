using KerioCertManager.Modules.Kerio.Api;
using KerioCertManager.Modules.Kerio.Client;
using KerioCertManager.Modules.Kerio.Domain;
using KerioCertManager.Modules.Kerio.Notifications;
using KerioCertManager.Modules.Kerio.Sync;
using KerioCertManager.Modules.KeyManager.Contracts;
using Modules.Core.Errors;

namespace KerioCertManager.Modules.Kerio.Tests;

/// <summary>Несколько сертификатов на сервере: проверка имён по KMP и решения по сертификатам своих имён.</summary>
public sealed class ExtrasTests
{
    private static readonly DateTimeOffset Сейчас = new(2026, 10, 30, 12, 0, 0, TimeSpan.Zero);

    private const string Имя = "webmail.example.com";

    [Fact]
    public void Имена_приводятся_к_написанию_KMP()
    {
        var (поУмолчанию, свои) = CertificateNames.Check("MAIL.example.com", ["WebMail.Example.com"], Kmp("mail.example.com", Имя), []);

        поУмолчанию.ShouldBe("mail.example.com");
        свои.ShouldBe([Имя]);
    }

    [Fact]
    public void Чего_нет_в_KMP_не_сохраняется_с_подсказкой()
    {
        var отказ = Should.Throw<AppException>(() => CertificateNames.Check(null, ["webmial.example.com"], Kmp(Имя), []));

        отказ.Code.ShouldBe(ErrorCodes.Validation);
        отказ.Message.ShouldContain("«webmial.example.com» — нет в Key Manager Plus");
        отказ.Message.ShouldContain("возможно, " + Имя);
    }

    [Fact]
    public void Привязанный_и_пропавший_из_KMP_не_мешает_сохранить()
    {
        var (_, свои) = CertificateNames.Check(null, ["old.example.com", Имя], Kmp(Имя), ["old.example.com"]);

        свои.ShouldBe(["old.example.com", Имя]);
    }

    [Theory]
    [InlineData("web mail.example.com", "пробел")]
    [InlineData("", "пустое имя")]
    public void Негодное_имя_с_причиной(string имя, string причина) =>
        Should.Throw<AppException>(() => CertificateNames.Check(null, [имя], Kmp(Имя), [])).Message.ShouldContain(причина);

    [Fact]
    public void Сертификат_по_умолчанию_и_своего_имени_одновременно_нельзя() =>
        Should.Throw<AppException>(() => CertificateNames.Check(Имя, [Имя], Kmp(Имя), [])).Message.ShouldContain("указан дважды");

    [Fact]
    public void Пустой_KMP_называет_причину() =>
        Should.Throw<AppException>(() => CertificateNames.Check(Имя, [], [], [])).Message.ShouldContain("не отдал ни одного");

    [Theory]
    [InlineData("webmial.example.com", Имя)]
    [InlineData("mail.example.org", null)]
    public void Подсказка_только_для_похожего(string имя, string? ждём) =>
        CertificateNames.Ближайшее(имя, [Имя, "vpn.example.com"]).ShouldBe(ждём);

    [Fact]
    public void Нет_в_хранилище_ставится()
    {
        var план = Решить(Сервер(), Доп(), Выпуск("bb", 90));

        план.Action.ShouldBe(PlanAction.Deploy);
        план.Trigger.ShouldBe(DeployTriggers.Initial);
        план.Behind.ShouldBeTrue();
    }

    [Fact]
    public void Продление_ставится_как_продление()
    {
        var сервер = Сервер(Лежит("aa", 20));
        var план = Решить(сервер, Доп(installed: "aa"), Выпуск("bb", 90));

        план.Action.ShouldBe(PlanAction.Deploy);
        план.Trigger.ShouldBe(DeployTriggers.Renewal);
        план.Alerts.ShouldContain(а => а.Kind == AlertKinds.Expiring && а.Scope == Имя, "старая версия истекает — новая ждёт установки");
    }

    [Fact]
    public void Лежит_текущий_признаётся_без_установки()
    {
        var сервер = Сервер(Лежит("bb", 90));

        Решить(сервер, Доп(), Выпуск("bb", 90)).Action.ShouldBe(PlanAction.Adopt);
        Решить(сервер, Доп(installed: "bb", sha: "SHA-bb"), Выпуск("bb", 90)).Action.ShouldBe(PlanAction.None);
    }

    /// <summary>Как с сертификатом по умолчанию: доверенный чужой, что действует дольше выпуска KMP, автоматически не меняется.</summary>
    [Fact]
    public void Чужой_дольше_выпуска_KMP_не_заменяется()
    {
        var сервер = Сервер(Лежит("ff", 300));
        var выпуск = Выпуск("bb", 90);
        var план = Решить(сервер, Доп(), выпуск);

        план.Action.ShouldBe(PlanAction.None);
        план.Behind.ShouldBeFalse();

        var вид = ServerView.Доп(сервер, Доп(), new Dictionary<string, SourceCertificate> { [Имя] = выпуск }, 21, Сейчас, dryRun: false);
        вид.Status.ShouldBe(ServerStatuses.Ok);
        вид.StatusText.ShouldContain("не из KMP");

        ExtraPlanner.Plan(сервер, Доп(), выпуск, storeKnown: true, globalAutoDeploy: true, warningDays: 21, Сейчас, manual: true)
            .Action.ShouldBe(PlanAction.Deploy);
    }

    /// <summary>Свои прежние выпуски — не чужие, даже если KMP при продлении их заменил: короткий перевыпуск ставится.</summary>
    [Fact]
    public void Свои_прежние_выпуски_не_чужие()
    {
        var доп = Доп(installed: "cc");
        доп.PreviousSerials = ["aa"];

        Решить(Сервер(Лежит("aa", 300), Лежит("cc", 200)), доп, Выпуск("bb", 30)).Action.ShouldBe(PlanAction.Deploy);
    }

    /// <summary>Память поставленных: сменённый — первым, без повторов и не длиннее предела.</summary>
    [Fact]
    public void Память_поставленных_выпусков()
    {
        InstalledHistory.Push(null, [], "aa").ShouldBeEmpty();
        InstalledHistory.Push("aa", ["bb"], "AA").ShouldBe(["bb"], "тот же выпуск — не смена");
        InstalledHistory.Push("aa", ["bb", "aa"], "cc").ShouldBe(["aa", "bb"]);
        InstalledHistory.Push("a0", [.. Enumerable.Range(1, 15).Select(н => $"a{н}")], "ff").Length.ShouldBe(InstalledHistory.MaxPrevious);
    }

    [Fact]
    public void Хранилище_не_прочитано_вслепую_не_ставим() =>
        ExtraPlanner.Plan(Сервер(), Доп(), Выпуск("bb", 90), storeKnown: false, true, 21, Сейчас, manual: false)
            .Action.ShouldBe(PlanAction.None);

    [Fact]
    public void Автоустановка_выключена_продление_ждёт_кнопки()
    {
        var сервер = Сервер(Лежит("aa", 30));
        сервер.AutoDeploy = false;

        var план = Решить(сервер, Доп("aa", "SHA-aa"), Выпуск("bb", 90));

        план.Action.ShouldBe(PlanAction.None);
        план.Alerts.ShouldContain(а => а.Kind == AlertKinds.RenewalPending && а.Scope == Имя);

        ExtraPlanner.Plan(сервер, Доп("aa", "SHA-aa"), Выпуск("bb", 90), true, true, 21, Сейчас, manual: true).Trigger.ShouldBe(DeployTriggers.Manual);
    }

    /// <summary>⚠ Выключатель сервера — о продлениях: только что добавленный сертификат ставится сразу; общая пауза держит и его.</summary>
    [Fact]
    public void Автоустановка_выключена_новый_сертификат_ставится()
    {
        var сервер = Сервер();
        сервер.AutoDeploy = false;

        var план = Решить(сервер, Доп(), Выпуск("bb", 90));

        план.Action.ShouldBe(PlanAction.Deploy);
        план.Trigger.ShouldBe(DeployTriggers.Initial);

        ExtraPlanner.Plan(сервер, Доп(), Выпуск("bb", 90), true, globalAutoDeploy: false, 21, Сейчас, manual: false)
            .Action.ShouldBe(PlanAction.None, "общая пауза");

        var kmp = new Dictionary<string, SourceCertificate> { [Имя] = Выпуск("bb", 90) };
        ServerView.Доп(сервер, Доп(), kmp, 21, Сейчас, dryRun: false).StatusText.ShouldContain("в очереди на установку");
        ServerView.Доп(сервер, Доп(), kmp, 21, Сейчас, dryRun: false, paused: true).StatusText.ShouldContain("ждёт ручной установки");

        var продление = Сервер(Лежит("aa", 30));
        продление.AutoDeploy = false;
        ServerView.Доп(продление, Доп("aa", "SHA-aa"), kmp, 21, Сейчас, dryRun: false).StatusText
            .ShouldContain("ждёт ручной установки", customMessage: "продление ждёт кнопки");
        ServerView.Доп(продление, Доп("aa", "SHA-aa"), kmp, 21, Сейчас, dryRun: false, queued: true).StatusText
            .ShouldContain("в очереди на установку", customMessage: "кнопку нажали — в очереди");
    }

    [Fact]
    public void После_неудачи_ждёт_паузу()
    {
        var доп = Доп();
        доп.FailedSerial = "bb";
        доп.Failures = 1;
        доп.NextAttemptAt = Сейчас.AddMinutes(4);

        Решить(Сервер(), доп, Выпуск("bb", 90)).Action.ShouldBe(PlanAction.None);

        доп.NextAttemptAt = Сейчас.AddMinutes(-1);
        Решить(Сервер(), доп, Выпуск("bb", 90)).Trigger.ShouldBe(DeployTriggers.Retry);
    }

    /// <summary>⚠ Свежий лежит, а по имени подаётся прежний — Kerio держит старую версию: тревога, а не повторная установка.</summary>
    [Fact]
    public void Свежий_лежит_а_по_имени_подаётся_прежний()
    {
        var сервер = Сервер(Лежит("bb", 90));
        сервер.ServedByName[Sni.For(Имя)!] = "SHA-aa";

        var план = Решить(сервер, Доп(installed: "bb", sha: "SHA-bb"), Выпуск("bb", 90));

        план.Action.ShouldBe(PlanAction.None);
        план.Alerts.ShouldHaveSingleItem().Kind.ShouldBe(AlertKinds.NameStale);
    }

    [Fact]
    public void Нет_в_KMP_пишется_про_это_имя() =>
        Решить(Сервер(), Доп(), null).Alerts.ShouldHaveSingleItem().Scope.ShouldBe(Имя);

    [Theory]
    [InlineData("*.example.com", "kcm-check.example.com")]
    [InlineData("Mail.Example.com.", "mail.example.com")]
    [InlineData("10.0.0.1", null)]
    [InlineData("localhost", null)]
    public void Имя_для_SNI(string cn, string? ждём) => Sni.For(cn).ShouldBe(ждём);

    /// <summary>Предупреждения об одном виде по разным именам — разные письма, а не одно на всех.</summary>
    [Fact]
    public void Ключ_повтора_различает_имена()
    {
        var a = new ServerAlert(AlertKinds.Expiring, AlertLevel.Warning, "a", "a", "a.example.com");
        var б = new ServerAlert(AlertKinds.Expiring, AlertLevel.Warning, "б", "б", "b.example.com");
        var свой = new ServerAlert(AlertKinds.Expiring, AlertLevel.Warning, "в", "в");

        KerioNotifier.Ключ(a, 7).ShouldBe("expiring:7@a.example.com");
        KerioNotifier.Ключ(б, null).ShouldBe("expiring@b.example.com");
        KerioNotifier.Ключ(свой, null).ShouldBe("expiring");
    }

    private static ExtraPlan Решить(KerioServer сервер, ExtraCertificate доп, SourceCertificate? выпуск) =>
        ExtraPlanner.Plan(сервер, доп, выпуск, storeKnown: true, globalAutoDeploy: true, warningDays: 21, Сейчас, manual: false);

    private static IReadOnlyList<SourceCertificate> Kmp(params string[] имена) =>
        [.. имена.Select(и => new SourceCertificate(и, "01", null, Сейчас, Сейчас))];

    private static KerioServer Сервер(params StoredCertificate[] хранилище) => new()
    {
        Id = Guid.CreateVersion7(),
        Name = "kerio",
        BaseUrl = "https://kerio.example.com:4040",
        Username = "kcm",
        TlsMode = TlsModes.Pin,
        CommonName = "mail.example.com",
        AutoDeploy = true,
        Store = [.. хранилище],
        StoreCheckedAt = Сейчас,
    };

    private static ExtraCertificate Доп(string? installed = null, string? sha = null) =>
        new() { CommonName = Имя, AddedAt = Сейчас.AddDays(-1), InstalledSerial = installed, InstalledSha256 = sha };

    private static StoredCertificate Лежит(string serial, int days) =>
        new("server" + serial, KerioCertificate.Inactive, Имя, "Issuing CA", Сейчас.AddDays(days - 90), Сейчас.AddDays(days),
            serial, "SHA-" + serial, [Имя], false, false);

    private static SourceCertificate Выпуск(string serial, int days) =>
        new(Имя, serial, DateOnly.FromDateTime(Сейчас.AddDays(days).UtcDateTime), Сейчас.AddHours(-1), Сейчас);
}
