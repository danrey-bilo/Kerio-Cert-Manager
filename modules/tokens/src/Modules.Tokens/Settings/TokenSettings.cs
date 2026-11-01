using Modules.Core.Contracts.Settings;

namespace Modules.Tokens.Settings;

/// <summary>Настройки модуля токенов.</summary>
internal static class TokenSettings
{
    /// <summary>Сколько живёт токен доступа, минут.</summary>
    public static readonly SettingKey<int> AccessTtlMinutes = new("auth.access_ttl_minutes",
        15,
        "Access token lifetime, minutes",
        "",
        "auth",
        IsSecurity: true,
        Minimum: 1, Maximum: 1440);

    /// <summary>Через сколько простоя сессия кончается.</summary>
    public static readonly SettingKey<int> SessionIdleMinutes = new("auth.session_idle_minutes",
        60,
        "Idle timeout, minutes",
        "",
        "auth",
        IsSecurity: true,
        Minimum: 1, Maximum: 10_080);

    /// <summary>Сколько сессия живёт вообще, сколько бы ею ни пользовались.</summary>
    public static readonly SettingKey<int> SessionAbsoluteHours = new("auth.session_absolute_hours",
        12,
        "Absolute session lifetime, hours",
        "",
        "auth",
        IsSecurity: true,
        Minimum: 1, Maximum: 720);

    /// <summary>Сколько живёт токен настольного клиента, дней.</summary>
    public static readonly SettingKey<int> DeviceAbsoluteDays = new("auth.device_absolute_days",
        30,
        "Desktop client credential lifetime, days",
        "",
        "auth",
        IsSecurity: true,
        Minimum: 1, Maximum: 365);

    /// <summary>Окно снисхождения при повторном обновлении, секунд.</summary>
    public static readonly SettingKey<int> RefreshGraceSeconds = new("auth.refresh_grace_seconds",
        10,
        "Refresh grace window, seconds",
        "",
        "auth",
        IsSecurity: true,
        Minimum: 0, Maximum: 300);

    /// <summary>Сколько держать закрытые строки, дней.</summary>
    public static readonly SettingKey<int> RetentionDays = new("auth.session_retention_days",
        30,
        "Keep closed sessions, days",
        "",
        "auth",
        Minimum: 1, Maximum: 3650);

    /// <summary>Сколько ключей может завести один человек.</summary>
    public static readonly SettingKey<int> MaxPerUser = new("apitokens.max_per_user",
        10,
        "API keys per person",
        "",
        "apitokens",
        IsSecurity: true,
        Minimum: 1, Maximum: 1000);

    /// <summary>Наибольший срок жизни ключа, дней.</summary>
    public static readonly SettingKey<int> MaxLifetimeDays = new("apitokens.max_lifetime_days",
        365,
        "Maximum API key lifetime, days",
        "",
        "apitokens",
        IsSecurity: true,
        Minimum: 1, Maximum: 3650);

    /// <summary>Сколько держать мёртвые ключи, дней.</summary>
    public static readonly SettingKey<int> KeepDeadDays = new("apitokens.keep_dead_days",
        90,
        "Keep dead API keys, days",
        "",
        "apitokens",
        Minimum: 1, Maximum: 3650);
}

/// <summary>Объявление настроек модуля для общего экрана настроек.</summary>
internal sealed class TokenSettingDeclarations : ISettingDeclarationProvider
{
    public IReadOnlyList<ISettingDeclaration> Declarations { get; } =
    [
        TokenSettings.AccessTtlMinutes,
        TokenSettings.SessionIdleMinutes,
        TokenSettings.SessionAbsoluteHours,
        TokenSettings.DeviceAbsoluteDays,
        TokenSettings.RefreshGraceSeconds,
        TokenSettings.RetentionDays,
        TokenSettings.KeepDeadDays,

        // ⚠ Решение 0111: оба ключа читались с этапа 10.5.9, но объявлены не были.
        TokenSettings.MaxPerUser,
        TokenSettings.MaxLifetimeDays,
    ];
}
