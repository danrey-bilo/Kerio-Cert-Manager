using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Modules.Core.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace KerioCertManager.IntegrationTests;

/// <summary>Key Manager Plus через API: ключ API, проверка связи, снимок, выгрузка.</summary>
[Collection(KerioCertManagerHost.Collection)]
public sealed class KeyManagerApi(KerioCertManagerHost host) : IAsyncLifetime
{
    private const string Состояние = "/api/keymanager";
    private const string КлючApi = "/api/keymanager/api-key";
    private const string Проверка = "/api/keymanager/test";

    private const string Ключ = "A36ECFEF-1234-4C5D-8E9F-0A1B2C3D4E5F";

    /// <summary>Свой Common Name — не задеть соседей по коллекции.</summary>
    private const string Имя = "keymanager-api.example.test";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public ValueTask InitializeAsync()
    {
        host.User.SignIn();
        host.Settings.Values.Clear();
        host.Settings.Values["keymanager.base_url"] = "https://kmp.example.test:6565";
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        host.User.SignIn();
        host.Settings.Values.Clear();

        var клиент = host.CreateClient();
        await клиент.PutAsJsonAsync(КлючApi, new { api_key = "" }, CancellationToken.None);

        await using (var scope = host.Services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<AppDbContext>().Database.ExecuteSqlInterpolatedAsync(
                $"DELETE FROM keymanager_certificates WHERE common_name = {Имя}", CancellationToken.None);
        }

        host.User.SignOut();
    }

    [Fact]
    public async Task Состояние_не_отдаёт_ключа_ни_в_каком_виде()
    {
        (await host.CreateClient().PutAsJsonAsync(КлючApi, new { api_key = Ключ }, Ct))
            .StatusCode.ShouldBe(HttpStatusCode.NoContent);

        var ответ = await host.CreateClient().GetAsync(Состояние, Ct);
        var тело = await ответ.Content.ReadAsStringAsync(Ct);

        ответ.StatusCode.ShouldBe(HttpStatusCode.OK);
        JsonNode.Parse(тело)!["api_key_set"]!.GetValue<bool>().ShouldBeTrue();
        тело.ShouldNotContain(Ключ);

        (await host.CreateClient().PutAsJsonAsync(КлючApi, new { api_key = "" }, Ct))
            .StatusCode.ShouldBe(HttpStatusCode.NoContent);

        (await host.CreateClient().GetFromJsonAsync<JsonObject>(Состояние, Ct))!["api_key_set"]!
            .GetValue<bool>().ShouldBeFalse();
    }

    [Fact]
    public async Task Ключ_API_без_свежего_фактора_отвергается()
    {
        host.User.StepUpAt = DateTimeOffset.UtcNow.AddMinutes(-11);
        var клиент = host.CreateClient();

        await ОтказФактора(await клиент.PutAsJsonAsync(КлючApi, new { api_key = Ключ }, Ct));

        (await клиент.GetAsync(Состояние, Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await клиент.GetAsync("/api/keymanager/certificates", Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await Код(await клиент.PostAsync(Проверка, null, Ct))).ShouldBe("KEYMANAGER_NOT_CONFIGURED");
    }

    [Fact]
    public async Task Свежий_код_пропускает_ключ()
    {
        host.User.StepUpAt = DateTimeOffset.UtcNow.AddMinutes(-9);
        var клиент = host.CreateClient();

        (await клиент.PutAsJsonAsync(КлючApi, new { api_key = Ключ }, Ct)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
    }

    /// <summary>⚠ Сервис только ставит сертификаты на Kerio.</summary>
    [Theory]
    [InlineData("/api/keymanager/certificates/download")]
    [InlineData("/api/keymanager/certificates/private-key")]
    public async Task Файлы_и_закрытые_ключи_наружу_не_отдаются(string адрес)
    {
        await СнимокAsync("0a1b2c");
        host.User.StepUpAt = DateTimeOffset.UtcNow;

        var ответ = await host.CreateClient().PostAsJsonAsync(
            адрес, new { common_name = Имя, serial_number = "0a1b2c", format = "KEY" }, Ct);

        ответ.StatusCode.ShouldBeOneOf(HttpStatusCode.NotFound, HttpStatusCode.MethodNotAllowed);
        (await ответ.Content.ReadAsStringAsync(Ct)).ShouldNotContain("PRIVATE KEY");
    }

    [Fact]
    public async Task Проверка_без_адреса_и_ключа_отвечает_409_не_настроено()
    {
        var ответ = await host.CreateClient().PostAsync(Проверка, null, Ct);

        ответ.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await Код(ответ)).ShouldBe("KEYMANAGER_NOT_CONFIGURED");
    }

    [Fact]
    public async Task Открытый_HTTP_отвергается_до_отправки_ключа()
    {
        host.Settings.Values["keymanager.base_url"] = "http://kmp.example.test:6565";
        await host.CreateClient().PutAsJsonAsync(КлючApi, new { api_key = Ключ }, Ct);

        var ответ = await host.CreateClient().PostAsync(Проверка, null, Ct);

        ответ.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await Код(ответ)).ShouldBe("KEYMANAGER_UNAVAILABLE");
        (await Причина(ответ)).ShouldContain("https://");
    }

    [Fact]
    public async Task Недоступный_сервер_приезжает_409_с_причиной()
    {
        host.Settings.Values["keymanager.base_url"] = "https://127.0.0.1:1";
        await host.CreateClient().PutAsJsonAsync(КлючApi, new { api_key = Ключ }, Ct);

        var ответ = await host.CreateClient().PostAsync(Проверка, null, Ct);

        ответ.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await Код(ответ)).ShouldBe("KEYMANAGER_UNAVAILABLE");
        (await Причина(ответ)).ShouldContain("недоступен");
    }

    [Fact]
    public async Task Опрос_будится_и_снимок_читается()
    {
        (await host.CreateClient().PostAsync("/api/keymanager/poll", null, Ct))
            .StatusCode.ShouldBe(HttpStatusCode.Accepted);

        (await host.CreateClient().GetAsync("/api/keymanager/certificates", Ct))
            .StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    private static async Task ОтказФактора(HttpResponseMessage ответ)
    {
        ответ.StatusCode.ShouldBe(HttpStatusCode.Forbidden);

        var конверт = JsonNode.Parse(await ответ.Content.ReadAsStringAsync(Ct))!;
        конверт["code"]!.GetValue<string>().ShouldBe("SECOND_FACTOR_REQUIRED");
        конверт["details"]!["window_minutes"]!.GetValue<int>().ShouldBe(10);
    }

    /// <summary>Пара «Common Name + серийный» в снимке, как после опроса.</summary>
    private async Task СнимокAsync(string серийный)
    {
        await using var scope = host.Services.CreateAsyncScope();

        await scope.ServiceProvider.GetRequiredService<AppDbContext>().Database.ExecuteSqlInterpolatedAsync(
            $"""
            INSERT INTO keymanager_certificates (common_name, serial_number, expires_on, first_seen_at, last_seen_at)
            VALUES ({Имя}, {серийный}, {new DateOnly(2027, 10, 2)}, now(), now())
            ON CONFLICT (common_name, serial_number) DO NOTHING
            """,
            Ct);
    }

    private static async Task<string> Код(HttpResponseMessage ответ) =>
        JsonNode.Parse(await ответ.Content.ReadAsStringAsync(Ct))!["code"]!.GetValue<string>();

    private static async Task<string> Причина(HttpResponseMessage ответ) =>
        JsonNode.Parse(await ответ.Content.ReadAsStringAsync(Ct))!["details"]!["reason"]!.GetValue<string>();
}
