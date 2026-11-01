using Modules.Settings.Settings;
using Modules.Settings.Storage;
using Modules.Settings.Values;
using Modules.Core.Contracts.Settings;

namespace Modules.Settings.Reading;

/// <summary>На каком языке писать вот этому человеку.</summary>
internal sealed class UserLocale(
    SettingsRepository repository, SettingsReader reader, SettingsModuleFeatures features) : IUserLocale
{
    public async Task<string> ForAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        // ⚠ Без предпочтений личный слой не читается вовсе (решение 0111).
        if (features.UserPreferences)
        {
            var свои = await repository.PreferencesAsync(userId, cancellationToken);

            if (свои.TryGetValue(UiSettings.Locale.Name, out var json)
                && SettingValues.Read(UiSettings.Locale, json) is string своё)
            {
                return своё;
            }
        }

        var ниже = await reader.ResolveAsync(UiSettings.Locale, cancellationToken);

        return ниже.Value as string ?? UiSettings.Locale.Default;
    }
}
