using Modules.Settings.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Modules.Settings.Reading;

/// <summary>Без экрана администратора строки в базе роняют старт.</summary>
internal sealed class StoredValuesGuard(IServiceScopeFactory scopes) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();

        var строки = await scope.ServiceProvider.GetRequiredService<SettingsRepository>().SystemAsync(cancellationToken);

        if (строки.Count > 0)
        {
            throw new InvalidOperationException(
                "Экран системных настроек выключен (settings.admin_api), а в system_settings лежат значения "
                + "администратора: " + string.Join(", ", строки.Keys.Order(StringComparer.Ordinal))
                + ". Без экрана они не действуют: перенесите их в конфигурацию (Settings:<ключ>) или "
                + "закрепление и удалите строки.");
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
