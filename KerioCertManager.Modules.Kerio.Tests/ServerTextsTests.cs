using System.Text.RegularExpressions;
using KerioCertManager.Modules.Kerio.Localization;

namespace KerioCertManager.Modules.Kerio.Tests;

/// <summary>Тексты сервера на языке читателя: шаблоны со вставками, вложенные фразы, словари в сборке.</summary>
public sealed class ServerTextsTests
{
    private static readonly ServerTexts Тексты = new(new Dictionary<string, IReadOnlyDictionary<string, string>>
    {
        ["en"] = new Dictionary<string, string>
        {
            ["Убраны."] = "Removed.",
            ["Установлен {0}, действует до {1}."] = "Installed {0}, valid until {1}.",
            ["Прежние версии не убраны: {0}"] = "Old versions not removed: {0}",
            ["{0}: {1}"] = "{0}: {1}",
            ["сертификат по умолчанию"] = "default certificate",
            ["Для {0} подаётся {1}"] = "{1} is served for {0}",
            ["Сервер недоступен."] = "Server unreachable.",
            ["Проверьте адрес."] = "Check the address.",
            ["Сертификат установлен.{0}"] = "Certificate installed.{0}",
            [" Прежние версии ({0}) убраны."] = " Previous versions ({0}) removed.",
        },
    });

    [Fact]
    public void Точный_текст_и_шаблон_со_вставками()
    {
        Тексты.Translate("Убраны.", "en").ShouldBe("Removed.");
        Тексты.Translate("Установлен mail.example.com, действует до 2027-01-22.", "en")
            .ShouldBe("Installed mail.example.com, valid until 2027-01-22.");
    }

    [Fact]
    public void Вставка_со_своей_фразой_переводится_тоже_и_порядок_вставок_по_переводу()
    {
        Тексты.Translate("Прежние версии не убраны: Сервер недоступен.", "en").ShouldBe("Old versions not removed: Server unreachable.");
        Тексты.Translate("Для mail.example.com подаётся сертификат по умолчанию", "en")
            .ShouldBe("default certificate is served for mail.example.com");
    }

    /// <summary>Хвост, приклеенный к фразе условием, — ключ с пробелом в начале — переводится и внутри неё.</summary>
    [Fact]
    public void Ключ_с_пробелом_по_краю_находится_во_вставке() =>
        Тексты.Translate("Сертификат установлен. Прежние версии (server4) убраны.", "en")
            .ShouldBe("Certificate installed. Previous versions (server4) removed.");

    [Fact]
    public void Точный_шаблон_главнее_общего() =>
        Тексты.Translate("Прежние версии не убраны: x", "en").ShouldBe("Old versions not removed: x");

    [Fact]
    public void Края_и_предложения_по_отдельности()
    {
        Тексты.Translate("  Убраны.\n", "en").ShouldBe("  Removed.\n");
        Тексты.Translate("Сервер недоступен. Проверьте адрес.", "en").ShouldBe("Server unreachable. Check the address.");
    }

    /// <summary>Ошибка шага в журнале — «validate: …» — переводится и с меткой шага впереди.</summary>
    [Fact]
    public void Метка_шага_перед_текстом() =>
        ServerTexts.FromAssembly().Translate(
                "validate: Выпуск из Key Manager Plus (2026-08-14) старше установленного (2026-10-31): сам сервис откат не делает. "
                + "Установите вручную, если это намеренно.", "en")
            .ShouldBe("validate: The Key Manager Plus release (2026-08-14) is older than the installed one (2026-10-31): "
                + "the service does not roll back on its own. Install it manually if this is intentional.");

    [Fact]
    public void Значок_перед_текстом() =>
        ServerTexts.FromAssembly().Translate("🟠 Сертификат mail.example.com истекает через 12 дн.", "en")
            .ShouldBe("🟠 Certificate mail.example.com expires in 12 d.");

    /// <summary>Хвост с кодом, приклеенный к тексту ошибки, и шаблон с более длинным разделителем при равной точности.</summary>
    [Fact]
    public void Хвост_и_равные_шаблоны()
    {
        var тексты = ServerTexts.FromAssembly();

        тексты.Translate("Kerio отказал (ConnectCertificate.importCertificate): Emulated failure. [код 1000]", "en")
            .ShouldBe("Kerio refused (ConnectCertificate.importCertificate): Emulated failure. [code 1000]");
        тексты.Translate("Сертификат mail.example.com от Demo CA, до 2027-01-30, SHA-256 AA:BB; промежуточных в цепочке: 1.", "en")
            .ShouldBe("Certificate mail.example.com from Demo CA, until 2027-01-30, SHA-256 AA:BB; intermediate certificates in the chain: 1.");
        тексты.Translate("Сертификат mail.example.com от Demo CA, до 2027-01-30, SHA-256 AA:BB.", "en")
            .ShouldBe("Certificate mail.example.com from Demo CA, until 2027-01-30, SHA-256 AA:BB.");
    }

    [Fact]
    public void Неизвестное_остаётся_как_есть()
    {
        Тексты.Translate("Совсем другой текст", "en").ShouldBe("Совсем другой текст");
        Тексты.Translate("Убраны.", "ru").ShouldBe("Убраны.");
        Тексты.Translate("Убраны.", "de").ShouldBe("Убраны.");
        Тексты.Translate("plain ascii", "en").ShouldBe("plain ascii");
    }

    /// <summary>⚠ Строку, присланную пользователем, перевод разбирает за линейное время, длинную не трогает.</summary>
    [Fact]
    public void Злонамеренная_строка_не_грузит_процессор()
    {
        var тексты = ServerTexts.FromAssembly();
        var кусок = string.Concat(Enumerable.Repeat("Сервер: «x» — нет: ", 200));
        var часы = System.Diagnostics.Stopwatch.StartNew();

        тексты.Translate(кусок[..ServerTexts.МаксДлина], "en");
        тексты.Translate(string.Concat(Enumerable.Repeat(кусок, 50)), "en").Length.ShouldBe(кусок.Length * 50);

        часы.Elapsed.ShouldBeLessThan(TimeSpan.FromMilliseconds(500));
    }

    [Theory]
    [InlineData("en-US,en;q=0.9,ru;q=0.8", "en")]
    [InlineData("uk", "uk")]
    [InlineData("de-DE, uk;q=0.5", "uk")]
    [InlineData("ru-RU", "ru")]
    [InlineData("fr", "ru")]
    [InlineData(null, "ru")]
    public void Язык_из_заголовка(string? заголовок, string язык) => ServerTexts.FromAcceptLanguage(заголовок).ShouldBe(язык);

    /// <summary>⚠ Словари в сборке: вставки перевода — те же, что у шаблона; английский и украинский — об одном.</summary>
    [Fact]
    public void Словари_в_сборке_согласованы()
    {
        var сборка = typeof(ServerTexts).Assembly;
        Dictionary<string, string> Словарь(string язык)
        {
            using var поток = сборка.GetManifestResourceStream($"KerioCertManager.Modules.Kerio.Localization.{язык}.json").ShouldNotBeNull();
            return System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(поток).ShouldNotBeNull();
        }

        var en = Словарь("en");
        var uk = Словарь("uk");

        en.Keys.Order(StringComparer.Ordinal).ShouldBe(uk.Keys.Order(StringComparer.Ordinal));

        static string Вставки(string текст) =>
            string.Join(",", Regex.Matches(текст, @"\{[^{}]+\}").Select(m => m.Value).Order(StringComparer.Ordinal));

        foreach (var (словарь, язык) in new[] { (en, "en"), (uk, "uk") })
        {
            foreach (var (шаблон, перевод) in словарь)
            {
                Вставки(перевод).ShouldBe(Вставки(шаблон), $"{язык}: «{шаблон}» → «{перевод}»");
            }
        }

        ServerTexts.FromAssembly().Translates("en").ShouldBeTrue();
    }
}
