using System.Text;
using KerioCertManager.Modules.KeyManager.Client;
using KerioCertManager.Modules.KeyManager.Domain;
using KerioCertManager.Modules.KeyManager.Secrets;
using KerioCertManager.Modules.KeyManager.Settings;
using Modules.Crypto;
using Modules.Core.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace KerioCertManager.Modules.KeyManager.Tests;

/// <summary>Ключ API: запечатан с адресом, открывается только для него, перекладывается при ротации.</summary>
[Collection(KeyManagerDatabaseCollection.Name)]
public sealed class ApiKeyTests(KeyManagerDatabase база) : IAsyncLifetime
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private const string Ключ = "75408B31-C7D6-4F5A-9A0E-1B2C3D4E5F60";

    private const string Адрес = "https://kmp.example.test:6565";

    private static readonly string Цель = ApiKeyStore.Target(Адрес);

    public ValueTask InitializeAsync()
    {
        база.Сброс();
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        база.Конверт.Вернуть();

        await using var scope = база.Scope();
        await scope.ServiceProvider.GetRequiredService<AppDbContext>()
            .Set<KeyManagerState>().ExecuteDeleteAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Ключ_лежит_запечатанным_и_открывается()
    {
        await Задать(Ключ);

        var строка = await Строка();
        строка.ApiKey.ShouldNotBeNull();
        строка.ApiKey.AsSpan().IndexOf(Encoding.UTF8.GetBytes(Ключ)).ShouldBe(-1, "ключ в базе не лежит открытым");
        строка.UpdatedBy.ShouldBe(база.Личность.Id);

        await using var scope = база.Scope();
        var хранилище = scope.ServiceProvider.GetRequiredService<ApiKeyStore>();
        (await хранилище.HasAsync(Ct)).ShouldBeTrue();
        (await хранилище.GetForAsync(Цель, Ct)).ShouldBe(Ключ);
    }

    [Fact]
    public async Task Пустая_строка_снимает_ключ()
    {
        await Задать(Ключ);
        await Задать("");

        await using var scope = база.Scope();
        var хранилище = scope.ServiceProvider.GetRequiredService<ApiKeyStore>();
        (await хранилище.HasAsync(Ct)).ShouldBeFalse();
        (await хранилище.GetForAsync(Цель, Ct)).ShouldBeNull();
    }

    [Fact]
    public async Task Ключ_не_открывается_для_другого_адреса()
    {
        // ⚠ Перехваченная сессия: адрес переставлен на чужой сервер.
        await Задать(Ключ);

        await using var scope = база.Scope();
        var хранилище = scope.ServiceProvider.GetRequiredService<ApiKeyStore>();

        var чужойАдрес = await Should.ThrowAsync<KeyManagerException>(
            () => хранилище.GetForAsync(ApiKeyStore.Target("https://evil.example.test:6565"), Ct));
        чужойАдрес.Message.ShouldContain("Введите ключ API заново");
        чужойАдрес.Message.ShouldNotContain(Ключ);

        await Should.ThrowAsync<KeyManagerException>(
            () => хранилище.GetForAsync(ApiKeyStore.Target("https://kmp.example.test:7070"), Ct));

        (await хранилище.GetForAsync(Цель, Ct)).ShouldBe(Ключ, "свой адрес по-прежнему открывает");
    }

    [Theory]
    [InlineData("https://KMP.Example.Test:6565/")]
    [InlineData(" https://kmp.example.test:6565 ")]
    [InlineData("https://kmp.example.test:6565/?x=1")]
    public void Один_адрес_в_разной_записи_одна_цель(string адрес)
    {
        ApiKeyStore.Target(адрес).ShouldBe(Цель);
    }

    [Fact]
    public void Умолчательный_порт_в_разной_записи_одна_цель()
    {
        ApiKeyStore.Target("https://kmp.example.test").ShouldBe(ApiKeyStore.Target("https://kmp.example.test:443/"));
        ApiKeyStore.Target("https://kmp.example.test:6565/kmp").ShouldNotBe(Цель);
    }

    [Fact]
    public async Task Ключ_введённый_при_закреплённом_отпечатке_вводится_заново()
    {
        await using var scope = база.Scope();
        var хранилище = scope.ServiceProvider.GetRequiredService<ApiKeyStore>();

        await хранилище.PutAsync(Ключ, Цель + " sha256=" + new string('A', 64), Ct);
        await scope.ServiceProvider.GetRequiredService<AppDbContext>().SaveChangesAsync(Ct);

        (await Should.ThrowAsync<KeyManagerException>(() => хранилище.GetForAsync(Цель, Ct)))
            .Message.ShouldContain("Введите ключ API заново");
    }

    [Fact]
    public async Task Соединитель_отказывает_сменившемуся_адресу_до_запроса()
    {
        база.Настройки.Значения[KeyManagerSettings.BaseUrl.Name] = Адрес;
        await Задать(Ключ);

        await using var scope = база.Scope();
        var соединитель = new KeyManagerConnector(
            база.Настройки, scope.ServiceProvider.GetRequiredService<ApiKeyStore>(), new KeyManagerModuleOptions());

        using (await соединитель.OpenAsync(Ct))
        {
        }

        база.Настройки.Значения[KeyManagerSettings.BaseUrl.Name] = "https://evil.example.test:6565";

        var ошибка = await Should.ThrowAsync<KeyManagerException>(() => соединитель.OpenAsync(Ct));
        ошибка.Message.ShouldContain("заново");
    }

    [Fact]
    public async Task Ротация_перекладывает_обёртку_и_ключ_открывается()
    {
        await Задать(Ключ);
        var было = (await Строка()).DataKey!;

        база.Конверт.Повернуть();

        await using (var scope = база.Scope())
        {
            var итог = await scope.ServiceProvider.GetRequiredService<ApiKeyRewrap>().RewrapAsync(10, null, Ct);
            итог.Rewrapped.ShouldBe(1);
            итог.Next.ShouldBeNull();
        }

        var стало = (await Строка()).DataKey!;
        стало.ShouldNotBe(было);
        база.Конверт.NeedsRewrap(new WrappedDataKey(стало)).ShouldBeFalse();

        await using (var scope = база.Scope())
        {
            (await scope.ServiceProvider.GetRequiredService<ApiKeyStore>().GetForAsync(Цель, Ct)).ShouldBe(Ключ);

            (await scope.ServiceProvider.GetRequiredService<ApiKeyRewrap>().RewrapAsync(10, null, Ct))
                .Rewrapped.ShouldBe(0);
        }
    }

    private async Task Задать(string значение)
    {
        await using var scope = база.Scope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        await scope.ServiceProvider.GetRequiredService<ApiKeyStore>().PutAsync(
            значение,
            ApiKeyStore.Target((string?)база.Настройки.Значения.GetValueOrDefault(KeyManagerSettings.BaseUrl.Name) ?? Адрес),
            Ct);
        await db.SaveChangesAsync(Ct);
    }

    private async Task<KeyManagerState> Строка()
    {
        await using var scope = база.Scope();

        return await scope.ServiceProvider.GetRequiredService<AppDbContext>()
            .Set<KeyManagerState>().AsNoTracking().SingleAsync(Ct);
    }
}
