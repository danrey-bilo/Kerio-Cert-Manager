using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace KerioCertManager.IntegrationTests;

/// <summary>Язык панели: личная настройка в профиле и тексты сервера на нём.</summary>
[Collection(KerioCertManagerHost.Collection)]
public sealed class LocalizationApi(KerioCertManagerHost host)
{
    private const string Предпочтения = "/api/me/preferences";

    private static readonly Regex Кириллица = new("[А-Яа-яЁёІіЇїЄєҐґ]");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Язык_в_профиле_сохраняется_умолчание_установки_русский()
    {
        host.User.SignIn();

        try
        {
            var клиент = host.CreateClient();

            (await Язык(клиент)).ShouldBe("ru", "умолчание установки — appsettings.json");

            (await клиент.PatchAsJsonAsync(Предпочтения, new { values = new Dictionary<string, string> { ["ui.locale"] = "uk" } }, Ct))
                .StatusCode.ShouldBe(HttpStatusCode.OK);
            (await Язык(клиент)).ShouldBe("uk");

            (await клиент.PatchAsJsonAsync(Предпочтения, new { values = new Dictionary<string, string> { ["ui.locale"] = "de" } }, Ct))
                .IsSuccessStatusCode.ShouldBeFalse("только ru, en, uk");
            (await Язык(клиент)).ShouldBe("uk");
        }
        finally
        {
            await host.CreateClient().PatchAsJsonAsync(Предпочтения, new { reset = new[] { "ui.locale" } }, CancellationToken.None);
            host.User.SignOut();
        }
    }

    /// <summary>Ошибка сервера составлена по-русски — панель на английском и украинском получает её на своём языке.</summary>
    [Theory]
    [InlineData("en", "https")]
    [InlineData("uk", "https")]
    public async Task Ошибка_на_языке_панели(string язык, string слово)
    {
        host.User.SignIn();

        try
        {
            var клиент = host.CreateClient();
            var тело = new { name = "Kerio", base_url = "http://kerio.example.com:4040", username = "api", password = "x", tls_mode = "ca" };

            using var поРусски = await клиент.PostAsJsonAsync("/api/kerio/servers", тело, Ct);
            var исходный = (await поРусски.Content.ReadFromJsonAsync<JsonObject>(Ct))!["message"]!.GetValue<string>();
            Кириллица.IsMatch(исходный).ShouldBeTrue(исходный);

            using var запрос = new HttpRequestMessage(HttpMethod.Post, "/api/kerio/servers") { Content = JsonContent.Create(тело) };
            запрос.Headers.AcceptLanguage.ParseAdd(язык);
            using var ответ = await клиент.SendAsync(запрос, Ct);

            ответ.StatusCode.ShouldBe(поРусски.StatusCode);
            ответ.Headers.Vary.ShouldContain("Accept-Language");
            var текст = (await ответ.Content.ReadFromJsonAsync<JsonObject>(Ct))!["message"]!.GetValue<string>();
            текст.ShouldNotBe(исходный);
            текст.ShouldContain(слово);

            if (язык == "en")
            {
                Кириллица.IsMatch(текст).ShouldBeFalse(текст);
            }
        }
        finally
        {
            host.User.SignOut();
        }
    }

    private static async Task<string> Язык(HttpClient клиент) =>
        (await клиент.GetFromJsonAsync<JsonObject>(Предпочтения, Ct))!["effective"]!["ui.locale"]!.GetValue<string>();
}
