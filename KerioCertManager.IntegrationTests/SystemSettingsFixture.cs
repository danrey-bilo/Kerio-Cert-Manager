namespace KerioCertManager.IntegrationTests;

/// <summary>Тест с настоящими настройками; <c>system_settings</c> чистится до и после.</summary>
public sealed class SystemSettingsFixture(KerioCertManagerHost host) : IAsyncDisposable
{
    /// <summary>Убрать оставшееся от прежних тестов и включить настоящие настройки.</summary>
    public static async Task<SystemSettingsFixture> НачатьAsync(KerioCertManagerHost host)
    {
        ArgumentNullException.ThrowIfNull(host);

        await host.ОчиститьНастройкиAsync();
        host.НастоящиеНастройки = true;

        return new SystemSettingsFixture(host);
    }

    public async ValueTask DisposeAsync()
    {
        host.НастоящиеНастройки = false;
        await host.ОчиститьНастройкиAsync();
    }
}
