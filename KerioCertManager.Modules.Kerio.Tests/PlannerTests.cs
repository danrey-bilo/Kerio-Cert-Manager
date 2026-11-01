using KerioCertManager.Modules.Kerio.Client;
using KerioCertManager.Modules.Kerio.Domain;
using KerioCertManager.Modules.Kerio.Sync;
using KerioCertManager.Modules.KeyManager.Contracts;

namespace KerioCertManager.Modules.Kerio.Tests;

/// <summary>Решение «ставить ли»: продление, первая привязка, ручная правка, выключатели, повтор, сроки.</summary>
public sealed class PlannerTests
{
    private static readonly DateTimeOffset Сейчас = new(2026, 10, 30, 12, 0, 0, TimeSpan.Zero);

    private const string Имя = "mail.example.com";

    [Fact]
    public void Без_привязки_только_наблюдение()
    {
        var план = Решить(Сервер(cn: null), Подан("aa", 100), Выпуск("bb", 200));

        план.Action.ShouldBe(PlanAction.None);
        план.Alerts.ShouldBeEmpty();
    }

    [Fact]
    public void Нет_в_KMP_предупреждение()
    {
        var план = Решить(Сервер(), Подан("aa", 100), null);

        план.Action.ShouldBe(PlanAction.None);
        план.Alerts.ShouldHaveSingleItem().Kind.ShouldBe(AlertKinds.SourceMissing);
    }

    [Fact]
    public void На_сервере_уже_текущий_признаётся_без_установки()
    {
        Решить(Сервер(), Подан("00AA", 100), Выпуск("aa", 100)).Action.ShouldBe(PlanAction.Adopt);
        Решить(Сервер(installed: "aa"), Подан("aa", 100), Выпуск("AA", 100)).Action.ShouldBe(PlanAction.None);
    }

    [Fact]
    public void Продление_в_KMP_ставится()
    {
        var план = Решить(Сервер(installed: "aa"), Подан("aa", 60), Выпуск("bb", 90));

        план.Action.ShouldBe(PlanAction.Deploy);
        план.Trigger.ShouldBe(DeployTriggers.Renewal);
        план.Source!.SerialNumber.ShouldBe("bb");
    }

    [Fact]
    public void Продление_когда_на_сервере_скоро_истекает()
    {
        Решить(Сервер(installed: "aa"), Подан("aa", 5), Выпуск("bb", 90)).Trigger.ShouldBe(DeployTriggers.Expiring);
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public void Выключатели_автоустановки_останавливают_и_пишут(bool глобально, bool сервер)
    {
        var с = Сервер(installed: "aa");
        с.AutoDeploy = сервер;

        var план = DeployPlanner.Plan(с, Подан("aa", 60), Выпуск("bb", 90), глобально, 21, Сейчас);

        план.Action.ShouldBe(PlanAction.None);
        план.Alerts.ShouldContain(а => а.Kind == AlertKinds.RenewalPending);
    }

    /// <summary>⚠ Выключатель сервера — о продлениях: первая установка выбранного сертификата идёт и при нём.</summary>
    [Fact]
    public void Первая_установка_при_выключенном_выключателе_сервера()
    {
        var с = Сервер();
        с.AutoDeploy = false;

        Решить(с, Подан("aa", 5), Выпуск("bb", 90)).Trigger.ShouldBe(DeployTriggers.Initial);
        DeployPlanner.Plan(с, Подан("aa", 5), Выпуск("bb", 90), false, 21, Сейчас).Action.ShouldBe(PlanAction.None, "общая пауза");
    }

    /// <summary>⚠ Сервис ставил bb, а сервер подаёт aa: правка вручную — не спорим, пишем.</summary>
    [Fact]
    public void Ручная_правка_на_сервере_не_перебивается()
    {
        var план = Решить(Сервер(installed: "bb"), Подан("aa", 300), Выпуск("bb", 90));

        план.Action.ShouldBe(PlanAction.None);
        план.Alerts.ShouldHaveSingleItem().Kind.ShouldBe(AlertKinds.Drift);
    }

    [Fact]
    public void Ручной_запрос_ставит_вопреки_выключателям_и_правке()
    {
        var с = Сервер(installed: "bb");
        с.AutoDeploy = false;
        с.DeployRequestedAt = Сейчас;

        var план = DeployPlanner.Plan(с, Подан("aa", 300), Выпуск("bb", 90), false, 21, Сейчас);

        план.Action.ShouldBe(PlanAction.Deploy);
        план.Trigger.ShouldBe(DeployTriggers.Manual);
        план.ClearManualRequest.ShouldBeTrue();
    }

    /// <summary>⚠ Порт администрирования уже подаёт текущий выпуск, но порты служб могли остаться на прежнем.</summary>
    [Fact]
    public void Ручной_запрос_когда_уже_стоит_ставит_заново_с_проверкой_портов()
    {
        var с = Сервер(installed: "bb");
        с.DeployRequestedAt = Сейчас;

        var план = Решить(с, Подан("bb", 90), Выпуск("bb", 90));

        план.Action.ShouldBe(PlanAction.Deploy);
        план.Trigger.ShouldBe(DeployTriggers.Manual);
        план.ClearManualRequest.ShouldBeTrue();
    }

    /// <summary>⚠ Кнопка у сертификата своего имени не переставляет сертификат по умолчанию: Kerio перезапустил бы HTTPS зря.</summary>
    [Fact]
    public void Ручной_запрос_на_другое_имя_не_трогает_сертификат_по_умолчанию()
    {
        var с = Сервер(installed: "bb");
        с.DeployRequestedAt = Сейчас;
        с.DeployRequestedName = "webmail.example.com";

        var план = Решить(с, Подан("bb", 90), Выпуск("bb", 90));

        план.Action.ShouldBe(PlanAction.None);
        план.ClearManualRequest.ShouldBeFalse();
        с.ManualFor("WEBMAIL.example.com").ShouldBeTrue("имя сравнивается без учёта регистра");

        с.DeployRequestedName = "MAIL.example.com";
        Решить(с, Подан("bb", 90), Выпуск("bb", 90)).Trigger.ShouldBe(DeployTriggers.Manual);
    }

    /// <summary>Тот же серийный, но другой отпечаток — не «свой»: идёт на сверку с выгрузкой KMP.</summary>
    [Fact]
    public void Тот_же_серийный_с_другим_отпечатком_не_свой()
    {
        var с = Сервер(installed: "bb");
        с.InstalledSha256 = "SHAbb";

        Решить(с, Подан("bb", 90), Выпуск("bb", 90)).Action.ShouldBe(PlanAction.None, "отпечаток совпал — свой");

        с.InstalledSha256 = "SHAчужой";
        Решить(с, Подан("bb", 90), Выпуск("bb", 90)).Action.ShouldBe(PlanAction.Adopt, "другой сертификат с тем же серийным");
    }

    [Fact]
    public void Первая_привязка_не_меняет_действующий_дольше()
    {
        Решить(Сервер(), Подан("aa", 300), Выпуск("bb", 90)).Action.ShouldBe(PlanAction.None);
    }

    [Fact]
    public void Первая_привязка_заменяет_самоподписанный_и_скоро_истекающий()
    {
        Решить(Сервер(), Подан("aa", 300, самоподписан: true), Выпуск("bb", 90)).Trigger.ShouldBe(DeployTriggers.Initial);
        Решить(Сервер(), Подан("aa", 10), Выпуск("bb", 5)).Trigger.ShouldBe(DeployTriggers.Initial);
    }

    [Fact]
    public void Без_ответа_по_TLS_вслепую_не_ставим()
    {
        Решить(Сервер(installed: "aa"), null, Выпуск("bb", 90)).Action.ShouldBe(PlanAction.None);
    }

    [Fact]
    public void Неудача_откладывает_повтор_до_срока()
    {
        var с = Сервер(installed: "aa");
        с.FailedSerial = "bb";
        с.DeployFailures = 2;
        с.NextAttemptAt = Сейчас.AddMinutes(10);

        Решить(с, Подан("aa", 60), Выпуск("bb", 90)).Action.ShouldBe(PlanAction.None);

        с.NextAttemptAt = Сейчас.AddMinutes(-1);
        Решить(с, Подан("aa", 60), Выпуск("bb", 90)).Trigger.ShouldBe(DeployTriggers.Retry);

        с.NextAttemptAt = Сейчас.AddMinutes(10);
        Решить(с, Подан("aa", 60), Выпуск("cc", 90)).Trigger.ShouldBe(DeployTriggers.Renewal);
    }

    [Fact]
    public void Истёкший_и_истекающий_без_замены_предупреждения()
    {
        var истёк = Решить(Сервер(installed: "aa"), Подан("aa", -1), Выпуск("aa", -1));
        истёк.Alerts.ShouldHaveSingleItem().Kind.ShouldBe(AlertKinds.Expired);
        истёк.Alerts[0].Level.ShouldBe(AlertLevel.Critical);

        var скоро = Решить(Сервер(installed: "aa"), Подан("aa", 14), Выпуск("aa", 14));
        скоро.Alerts.ShouldHaveSingleItem().Kind.ShouldBe(AlertKinds.Expiring);
        скоро.Alerts[0].Level.ShouldBe(AlertLevel.Warning);

        Решить(Сервер(installed: "aa"), Подан("aa", 3), Выпуск("aa", 3)).Alerts[0].Level.ShouldBe(AlertLevel.Critical);
        Решить(Сервер(installed: "aa"), Подан("aa", 60), Выпуск("aa", 60)).Alerts.ShouldBeEmpty();
    }

    [Theory]
    [InlineData(1, 5)]
    [InlineData(2, 15)]
    [InlineData(3, 30)]
    [InlineData(4, 60)]
    [InlineData(40, 60)]
    public void Паузы_повторов(int неудач, int минут) => DeployPlanner.Backoff(неудач).ShouldBe(TimeSpan.FromMinutes(минут));

    private static DeployPlan Решить(KerioServer сервер, ServedCertificate? подан, SourceCertificate? выпуск) =>
        DeployPlanner.Plan(сервер, подан, выпуск, globalAutoDeploy: true, warningDays: 21, Сейчас);

    private static KerioServer Сервер(string? cn = Имя, string? installed = null) => new()
    {
        Id = Guid.CreateVersion7(),
        Name = "kerio",
        BaseUrl = "https://kerio.example.com:4040",
        Username = "kcm",
        TlsMode = TlsModes.Pin,
        CommonName = cn,
        InstalledSerial = installed,
    };

    private static ServedCertificate Подан(string serial, int days, bool самоподписан = false) => new(
        "SHA" + serial,
        Имя,
        самоподписан ? Имя : "Issuing CA",
        serial.ToLowerInvariant().TrimStart('0'),
        Сейчас.AddDays(days - 90),
        Сейчас.AddDays(days),
        [Имя],
        самоподписан,
        !самоподписан,
        string.Empty);

    private static SourceCertificate Выпуск(string serial, int days) =>
        new(Имя, serial, DateOnly.FromDateTime(Сейчас.AddDays(days).UtcDateTime), Сейчас.AddDays(-1), Сейчас);
}
