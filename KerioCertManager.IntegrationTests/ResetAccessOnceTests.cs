using Npgsql;
using Testcontainers.PostgreSql;

namespace KerioCertManager.IntegrationTests;

/// <summary>Сброс доступа из окружения — один раз на пару «адрес + пароль».</summary>
public sealed class ResetAccessOnceTests : IAsyncLifetime
{
    private const string Адрес = "admin@example.test";

    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:16")
        .WithDatabase("kcm")
        .WithUsername("kcm")
        .WithPassword("тест-не-секрет")
        .Build();

    private readonly string _мастерКлюч = MigrateCommand.NewMasterKey();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync() => await _postgres.StartAsync();

    public async ValueTask DisposeAsync() => await _postgres.DisposeAsync();

    [Fact]
    public async Task Сброс_с_тем_же_паролем_не_повторяется_а_с_другим_применяется()
    {
        (await MigrateAsync(
            "--KCM_INIT_ADMIN_EMAIL=" + Адрес, "--KCM_INIT_ADMIN_PASSWORD=Первый-пароль-2026!")).ShouldBe(0);

        (await MigrateAsync(Сброс("Сброшенный-пароль-2026!"))).ShouldBe(0);
        var послеСброса = await ХешAsync();
        (await ОтметокAsync()).ShouldBe(1);

        await ВыполнитьAsync("UPDATE users SET password_hash = 'сменён-администратором' WHERE email = @a");
        (await MigrateAsync(Сброс("Сброшенный-пароль-2026!"))).ShouldBe(0);

        (await ХешAsync()).ShouldBe("сменён-администратором", "та же пара второй раз не применяется");

        (await MigrateAsync(Сброс("Ещё-один-пароль-2026!"))).ShouldBe(0);

        (await ХешAsync()).ShouldNotBe("сменён-администратором");
        (await ХешAsync()).ShouldNotBe(послеСброса);
        (await ОтметокAsync()).ShouldBe(2);
    }

    private static string[] Сброс(string пароль) =>
        ["--KCM_RESET_ACCESS_EMAIL=" + Адрес, "--KCM_RESET_ACCESS_PASSWORD=" + пароль];

    private Task<int> MigrateAsync(params string[] settings) =>
        MigrateCommand.RunAsync(_postgres.GetConnectionString(), _мастерКлюч, settings);

    private async Task<string> ХешAsync() =>
        (string)(await ЗначениеAsync("SELECT password_hash FROM users WHERE email = @a"))!;

    private async Task<long> ОтметокAsync() =>
        (long)(await ЗначениеAsync("SELECT count(*) FROM kcm_reset_access_marks"))!;

    private async Task<object?> ЗначениеAsync(string sql)
    {
        await using var db = new NpgsqlConnection(_postgres.GetConnectionString());
        await db.OpenAsync(Ct);
        await using var команда = new NpgsqlCommand(sql, db);
        команда.Parameters.AddWithValue("a", Адрес);

        return await команда.ExecuteScalarAsync(Ct);
    }

    private async Task ВыполнитьAsync(string sql)
    {
        await using var db = new NpgsqlConnection(_postgres.GetConnectionString());
        await db.OpenAsync(Ct);
        await using var команда = new NpgsqlCommand(sql, db);
        команда.Parameters.AddWithValue("a", Адрес);
        await команда.ExecuteNonQueryAsync(Ct);
    }
}
