using System.Text.RegularExpressions;
using KerioCertManager.Modules.Kerio.Domain;
using KerioCertManager.Modules.Kerio.Localization;
using KerioCertManager.Modules.Kerio.Notifications;
using KerioCertManager.Modules.Kerio.Sync;
using Modules.Core.Contracts.Mail;

namespace KerioCertManager.Modules.Kerio.Tests;

/// <summary>Сводное письмо об очереди установки: итог по каждому имени, счёт, уровень, перевод.</summary>
public sealed class QueueReportTests
{
    private static readonly DateTimeOffset До = new(2027, 1, 30, 12, 0, 0, TimeSpan.Zero);

    private static readonly Regex Кириллица = new("[А-Яа-яЁёІіЇїЄєҐґ]");

    /// <summary>⚠ Повтор после отказа в той же очереди заменяет отказ; пробы и незаконченные не в счёт; отказы — сверху.</summary>
    [Fact]
    public void По_последнему_исходу_имени_отказы_сверху()
    {
        var сводка = QueueReport.Build(
            "Kerio A",
            [
                new("a.demo.local", DeployOutcomes.Succeeded, До, null),
                new("b.demo.local", DeployOutcomes.Failed, null, "Kerio отказал"),
                new("c.demo.local", DeployOutcomes.RolledBack, null, "Новый не подаётся"),
                new("B.demo.local", DeployOutcomes.Succeeded, До, null),
                new("d.demo.local", DeployOutcomes.DryRun, null, null),
                new("e.demo.local", DeployOutcomes.Running, null, null),
            ]).ShouldNotBeNull();

        сводка.Installed.ShouldBe(2);
        сводка.Failed.ShouldBe(1);
        сводка.Level.ShouldBe(AlertLevel.Warning);
        сводка.Title.ShouldBe("🟠 Очередь установки на «Kerio A» пройдена: установлено 2 из 3");
        сводка.Message.ShouldBe("Очередь установки пройдена. Установлено и подтверждено по TLS: 2. Не удалось: 1.");
        сводка.Rows.ShouldBe(
        [
            new("c.demo.local", "Возвращён прежний", "Новый не подаётся"),
            new("a.demo.local", "Установлен", "Действует до 30.01.2027 12:00 UTC"),
            new("B.demo.local", "Установлен", "Действует до 30.01.2027 12:00 UTC"),
        ]);
    }

    [Fact]
    public void Все_встали_без_значка_уровень_сведений()
    {
        var сводка = QueueReport.Build("Kerio A", [new("a.demo.local", DeployOutcomes.Succeeded, null, null)]).ShouldNotBeNull();

        сводка.Level.ShouldBe(AlertLevel.Info);
        сводка.Title.ShouldBe("Очередь установки на «Kerio A» пройдена: установлено 1 из 1");
        сводка.Rows.ShouldHaveSingleItem().Details.ShouldBe("—");
    }

    [Fact]
    public void Только_пробные_прогоны_сводки_нет() =>
        QueueReport.Build("Kerio A", [new("a.demo.local", DeployOutcomes.DryRun, null, null)]).ShouldBeNull();

    /// <summary>Тема, текст и таблица сводки переводятся целиком — со значком впереди и с кириллицей в имени сервера.</summary>
    [Theory]
    [InlineData("Kerio A")]
    [InlineData("Почта Киев")]
    public void Сводка_на_английском_и_украинском(string сервер)
    {
        var тексты = ServerTexts.FromAssembly();
        var сводка = QueueReport.Build(
            сервер,
            [
                new("a.demo.local", DeployOutcomes.Succeeded, До, null),
                new("b.demo.local", DeployOutcomes.Failed, null, "Kerio отказал (ConnectCertificate.importCertificate): Emulated failure. [код 1000]"),
                new("c.demo.local", DeployOutcomes.RolledBack, null, null),
            ]).ShouldNotBeNull();

        тексты.Translate(сводка.Title, "en").ShouldBe($"🟠 Installation queue on “{сервер}” done: 1 of 3 installed");
        тексты.Translate(сводка.Title, "uk").ShouldBe($"🟠 Чергу встановлення на «{сервер}» пройдено: встановлено 1 з 3");
        тексты.Translate(сводка.Message, "en").ShouldBe("The installation queue is done. Installed and verified over TLS: 1. Failed: 2.");

        var en = сводка.Rows.Select(r => (тексты.Translate(r.Outcome, "en"), тексты.Translate(r.Details, "en"))).ToList();
        en.ShouldBe(
        [
            ("Not installed", "Kerio refused (ConnectCertificate.importCertificate): Emulated failure. [code 1000]"),
            ("Rolled back to previous", "—"),
            ("Installed", "Valid until 30.01.2027 12:00 UTC"),
        ]);

        foreach (var текст in сводка.Rows.SelectMany(r => new[] { r.Outcome, r.Details }).Append(сводка.Message).Where(т => Кириллица.IsMatch(т)))
        {
            Кириллица.IsMatch(тексты.Translate(текст, "en")).ShouldBeFalse(текст);
            Кириллица.IsMatch(тексты.Translate(текст, "uk")).ShouldBeTrue(текст);
        }
    }

    [Fact]
    public void Письмо_сводки_на_всех_языках_и_без_предела_частоты()
    {
        var письма = new KerioMail();

        письма.Templates.Where(ш => ш.Kind == KerioMail.Summary).Select(ш => ш.Locale)
            .ShouldBe([MailLocales.Ru, MailLocales.Uk, MailLocales.En], ignoreOrder: true);
        письма.Templates.Where(ш => ш.Kind == KerioMail.Summary).ShouldAllBe(ш => ш.Body.Contains("{table:" + KerioMail.TableName + "}"));
        письма.UnthrottledKinds.ShouldContain(KerioMail.Summary);

        foreach (var язык in new[] { MailLocales.Ru, MailLocales.Uk, MailLocales.En })
        {
            KerioMail.SummaryColumns(язык).Count.ShouldBe(3);
        }
    }
}
