using Modules.Core.Contracts.Settings;

namespace Modules.Settings.Settings;

/// <summary>Личные предпочтения интерфейса.</summary>
internal static class UiSettings
{
    /// <summary>Раздел на экране настроек.</summary>
    public const string Category = "ui";

    /// <summary>Тема оформления.</summary>
    public static readonly SettingKey<string> Theme = new(
        "ui.theme",
        Default: "system",
        "Theme",
        "Interface colour scheme. System — follow the operating system.",
        Category,
        Scope: SettingScope.User,
        Choices: ["light", "dark", "system"]);

    /// <summary>Язык интерфейса.</summary>
    public static readonly SettingKey<string> Locale = new(
        "ui.locale",
        Default: "en",
        "Language",
        "Interface language.",
        Category,
        Scope: SettingScope.User,
        Choices: ["uk", "ru", "en"]);

    /// <summary>Формат даты.</summary>
    public static readonly SettingKey<string> DateFormat = new(
        "ui.date_format",
        Default: "DD.MM.YYYY",
        "Date format",
        "How dates are shown in lists and on cards.",
        Category,
        Scope: SettingScope.User,
        Choices: ["DD.MM.YYYY", "YYYY-MM-DD", "MM/DD/YYYY"]);

    /// <summary>Плотность списков.</summary>
    public static readonly SettingKey<string> ListDensity = new(
        "ui.list_density",
        Default: "comfortable",
        "List density",
        "How much vertical space a row takes.",
        Category,
        Scope: SettingScope.User,
        Choices: ["comfortable", "compact"]);

    /// <summary>Через сколько очищать буфер обмена после копирования секрета.</summary>
    public static readonly SettingKey<int> ClipboardClearSeconds = new(
        "ui.clipboard_clear_seconds",
        Default: 30,
        "Clear clipboard after, seconds",
        "How long a copied secret stays in the clipboard.",
        Category,
        Scope: SettingScope.User,
        Minimum: 5,
        Maximum: 600);

    /// <summary>Через сколько прятать показанный секрет.</summary>
    public static readonly SettingKey<int> RevealHideSeconds = new(
        "ui.reveal_hide_seconds",
        Default: 30,
        "Hide revealed secret after, seconds",
        "How long a revealed secret stays on screen.",
        Category,
        Scope: SettingScope.User,
        Minimum: 5,
        Maximum: 600);
}

/// <summary>Объявление настроек интерфейса для общего экрана настроек.</summary>
internal sealed class SettingsSettingDeclarations : ISettingDeclarationProvider
{
    public IReadOnlyList<ISettingDeclaration> Declarations { get; } =
    [
        UiSettings.Theme,
        UiSettings.Locale,
        UiSettings.DateFormat,
        UiSettings.ListDensity,
        UiSettings.ClipboardClearSeconds,
        UiSettings.RevealHideSeconds,
    ];
}
