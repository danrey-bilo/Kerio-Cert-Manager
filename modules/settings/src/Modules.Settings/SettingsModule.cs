using Modules.Settings.Api;
using Modules.Settings.Permissions;
using Modules.Settings.Catalog;
using Modules.Settings.Reading;
using Modules.Settings.Settings;
using Modules.Settings.Storage;
using Modules.Core.Data;
using Modules.Core.Contracts.Audit;
using Modules.Core.Contracts.Authz;
using Modules.Core.Contracts.Data;
using Modules.Core.Contracts.Settings;
using Modules.Core.Features;
using Modules.Core.Composition;
using Microsoft.Extensions.DependencyInjection;

namespace Modules.Settings;

/// <summary>Параметры подключения модуля настроек.</summary>
public sealed class SettingsModuleOptions
{
    /// <summary>Закреплённые значения системных настроек: имя ключа → значение строкой, как в конфигурации.</summary>
    public IDictionary<string, string> PinnedValues { get; } = new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>Переключатели подвозможностей.</summary>
    public SettingsModuleFeatures Features { get; } = new();
}

/// <summary>Переключатели модуля настроек (решения 0111, 0112).</summary>
public sealed class SettingsModuleFeatures() : ModuleFeatures("settings")
{
    /// <summary>Личные предпочтения поверх системных настроек.</summary>
    [Feature("settings.user_preferences", Description = "Личные предпочтения человека поверх системных: язык, тема и прочее")]
    public bool UserPreferences { get; set; } = true;

    /// <summary>Значения администратора: экран системных настроек и их хранение в базе.</summary>
    [Feature("settings.admin_api", Description = "Значения администратора: экран системных настроек, хранение и правка в базе")]
    public bool AdminApi { get; set; } = true;

    /// <inheritdoc />
    protected override IEnumerable<FeatureOwnership> Owned() =>
    [
        new("settings.admin_api",
            Permissions: [Permissions.SettingsPermissions.Manage],
            Actions: [Contracts.SettingsActions.SystemUpdate.Name]),
    ];
}

/// <summary>Подключение модуля настроек.</summary>
public static class SettingsModule
{
    /// <summary>Подключить настройки.</summary>
    public static IMvcBuilder AddSettingsModule(this IMvcBuilder mvc, Action<SettingsModuleOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(mvc);

        var options = new SettingsModuleOptions();

        configure?.Invoke(options);

        var services = mvc.Services;

        services.AddSingleton(options);

        services.AddSingleton<IEntityConfigurationProvider, SettingsEntities>();

        // ⚠ Свой набор миграций (решение 0114): проект применяет его своей командой миграций.
        services.AddModuleSchema(Schema.SettingsSchema.Declaration);

        services.AddScoped<SettingsRepository>();

        // ⚠ Читатель — ОБЛАСТЬ ЗАПРОСА, и это не кэш: снимок таблицы живёт ровно один запрос, правка настройки действует со следующего.
        services.AddScoped<SettingsReader>();
        services.AddScoped<ISettingsReader>(sp => sp.GetRequiredService<SettingsReader>());

        // ⚠ Решение 0117: какие настройки человек видит и правит, решает движок прав.
        services.Require<IPermissionChecker>("settings", "экрану настроек некому сказать, что человеку можно");

        services.AddSingleton<SettingsCatalog>();

        // ⚠ И трогается ПРИ СТАРТЕ, а не лениво.
        services.AddHostedService<SettingsCatalogService>();

        // ⚠ «Какой язык у вот этого человека».
        services.AddScoped<IUserLocale, UserLocale>();

        if (options.Features.AdminApi)
        {
            services.AddScoped<SettingsService>();

            // ⚠ Второй вопрос о праве на настройки защиты пишет решение сам (решение 0118): без писателя отказ в ослаблении политики не оставил бы строки.
            services.Require<IAuthorizationAuditor>("settings", "отказ в правке настроек защиты не попадёт в журнал");
        }
        else
        {
            services.AddHostedService<StoredValuesGuard>();
        }

        if (options.Features.UserPreferences)
        {
            services.AddScoped<PreferencesService>();
        }

        services.AddSingleton(options.Features.Filter(new SettingsSettingDeclarations()));
        services.AddSingleton(options.Features.Filter(new SettingsOwnActions()));
        services.AddSingleton(options.Features.Filter(new SettingsPermissions()));

        // ⚠ Без этого `internal`-контроллеры не находятся, и получается 404 без единой ошибки при старте.
        return mvc.AddModule(typeof(SettingsModule).Assembly, options.Features);
    }
}
