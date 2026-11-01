using Modules.Settings.Catalog;
using Modules.Settings.Storage;
using Modules.Settings.Values;
using Modules.Core.Contracts.Settings;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Modules.Settings.Reading;

/// <summary>Откуда взялось действующее значение настройки.</summary>
internal enum SettingsSource
{
    /// <summary>Закреплено проектом (<see cref="SettingsModuleOptions.PinnedValues"/>, решение 0112).</summary>
    PinnedByProject,

    /// <summary>Строка в <c>system_settings</c>.</summary>
    System,

    /// <summary>Раздел <c>Settings:</c> конфигурации — файл, окружение, секрет.</summary>
    Configuration,

    /// <summary>Умолчание из объявления ключа.</summary>
    Default,
}

/// <summary>Действующее значение и его источник.</summary>
internal readonly record struct SettingValue(object? Value, SettingsSource Source);

/// <summary>Настоящий читатель настроек: закреплённое → база → конфигурация → умолчание.</summary>
internal sealed class SettingsReader(
    SettingsRepository repository,
    SettingsCatalog catalog,
    IConfiguration configuration,
    ILogger<SettingsReader> log,
    SettingsModuleOptions? options = null) : ISettingsReader
{
    /// <summary>Раздел конфигурации, где ищутся значения.</summary>
    public const string Section = "Settings";

    private IReadOnlyDictionary<string, string>? _snapshot;

    public async ValueTask<T> GetAsync<T>(
        SettingKey<T> key, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(key);

        var найдено = await ResolveAsync(key, cancellationToken);

        return Действующее(key, найдено);
    }

    /// <summary>Значение для кода: заданное — как есть, в том числе заданное пустым.</summary>
    internal static T Действующее<T>(SettingKey<T> key, SettingValue найдено)
    {
        ArgumentNullException.ThrowIfNull(key);

        if (найдено.Value is T значение)
        {
            return значение;
        }

        return найдено.Value is null && найдено.Source != SettingsSource.Default && key.IsNullable
            ? default!
            : key.Default;
    }

    /// <summary>Действующее значение вместе с источником — для экрана настроек.</summary>
    public async ValueTask<SettingValue> ResolveAsync(
        ISettingDeclaration declaration, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(declaration);

        // ⚠ Закреплённое — первым и без обращения к базе (решение 0112).
        if (options?.PinnedValues.TryGetValue(declaration.Name, out var закреплено) == true
            && TryPinned(declaration, закреплено, out var значениеЗакреплённое))
        {
            return new SettingValue(значениеЗакреплённое, SettingsSource.PinnedByProject);
        }

        // ⚠ Без экрана администратора слой базы не читается вовсе (решение 0112): строки, которые некому править, перекрывали бы конфигурацию.
        _snapshot ??= options?.Features.AdminApi == false
            ? new Dictionary<string, string>(StringComparer.Ordinal)
            : await repository.SystemAsync(cancellationToken);

        if (_snapshot.TryGetValue(declaration.Name, out var json))
        {
            // ⚠ ⚠ Годность спрашивается отдельно от значения (замечание ревью к PR #32).
            if (SettingValues.TryRead(declaration, json, out var значение))
            {
                return new SettingValue(значение, SettingsSource.System);
            }

            // ⚠ Громко, а не молча.
            log.LogWarning(
                "setting_value_invalid {Setting} {Type}",
                declaration.Name,
                SettingValues.Имя(declaration));
        }

        // ⚠ Конфигурация читается по ПОЛНОМУ имени ключа внутри раздела: `Settings:audit.retention_months`.
        var изФайла = configuration.GetSection(Section).GetSection(declaration.Name);

        if (изФайла.Value is not null)
        {
            // ⚠ Тем же разбором, что закрепление (ревизия 2026-10-26, решение 0118): пустая строка у обнуляемого ключа — «пусто», а не ошибка.
            if (TryPinned(declaration, изФайла.Value, out var разобрано))
            {
                return new SettingValue(разобрано, SettingsSource.Configuration);
            }

            log.LogWarning(
                "setting_value_invalid_configuration {Setting} {Type}",
                declaration.Name,
                SettingValues.Имя(declaration));
        }

        return new SettingValue(declaration.DefaultValue, SettingsSource.Default);
    }

    /// <summary>Значение из конфигурации: там всё строки.</summary>
    internal static bool TryPinned(ISettingDeclaration declaration, string текст, out object? значение)
    {
        ArgumentNullException.ThrowIfNull(declaration);
        ArgumentNullException.ThrowIfNull(текст);

        if (текст.Trim().Length == 0)
        {
            return SettingValues.TryRead(declaration, "null", out значение);
        }

        значение = FromConfiguration(declaration, текст);

        return значение is not null;
    }

    private static object? FromConfiguration(ISettingDeclaration declaration, string текст)
    {
        var тип = Nullable.GetUnderlyingType(declaration.ValueType) ?? declaration.ValueType;

        // ⚠ ⚠ Логическое — через `bool.TryParse`, а не как есть (ревью 2026-09-15, settings-2).
        var json = тип == typeof(bool)
            ? (bool.TryParse(текст.Trim(), out var логическое) ? (логическое ? "true" : "false") : текст)
            : тип == typeof(int) || тип == typeof(long)
                ? текст
                : System.Text.Json.JsonSerializer.Serialize(текст);

        return SettingValues.Read(declaration, json);
    }

    /// <summary>Забыть снимок: следующее чтение сходит в базу заново.</summary>
    public void Забыть() => _snapshot = null;

    /// <summary>Все объявления вместе с действующими значениями — для экрана настроек.</summary>
    public async Task<IReadOnlyList<(ISettingDeclaration Declaration, SettingValue Value)>> AllAsync(
        CancellationToken cancellationToken = default)
    {
        var итог = new List<(ISettingDeclaration, SettingValue)>(catalog.All.Count);

        foreach (var объявление in catalog.All)
        {
            итог.Add((объявление, await ResolveAsync(объявление, cancellationToken)));
        }

        return итог;
    }
}
