using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Modules.Settings.Catalog;

/// <summary>Собирает справочник настроек при старте.</summary>
internal sealed class SettingsCatalogService(
    IServiceProvider services, ILogger<SettingsCatalogService> log) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        var catalog = services.GetRequiredService<SettingsCatalog>();

        if (services.GetService<SettingsModuleOptions>() is { } options)
        {
            CheckPinned(catalog, options.PinnedValues);
        }

        log.LogInformation(
            "settings_catalog_ready {Settings} {Categories}",
            catalog.All.Count,
            catalog.All.Select(declaration => declaration.Category).Distinct(StringComparer.Ordinal).Count());

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>Закреплённые значения годны: ключ объявлен, он системный, значение разбирается.</summary>
    internal static void CheckPinned(SettingsCatalog catalog, IDictionary<string, string> pinned)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(pinned);

        var беды = new List<string>();

        foreach (var (имя, текст) in pinned.OrderBy(пара => пара.Key, StringComparer.Ordinal))
        {
            if (catalog.Find(имя) is not { } объявление)
            {
                беды.Add($"{имя}: такой настройки не объявляет ни один модуль");
            }
            else if (объявление.Scope != Modules.Core.Contracts.Settings.SettingScope.System)
            {
                беды.Add($"{имя}: личное предпочтение не закрепляется");
            }
            else if (!Reading.SettingsReader.TryPinned(объявление, текст ?? string.Empty, out _))
            {
                беды.Add($"{имя}: значение не годится для типа {Values.SettingValues.Имя(объявление)}");
            }
        }

        if (беды.Count > 0)
        {
            throw new InvalidOperationException(
                "Закреплённые настройки негодны (решение 0112): " + string.Join("; ", беды) + ".");
        }
    }
}
