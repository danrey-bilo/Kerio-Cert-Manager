using Modules.Core.Contracts.Settings;

namespace Modules.Identity.Settings;

/// <summary>Настройки модуля личностей.</summary>
internal static class IdentitySettings
{
    /// <summary>Минимальная длина пароля.</summary>
    public static readonly SettingKey<int> PasswordMinLength = new("auth.password_min_length",
        Default: 12,
        "Minimum password length",
        "How many characters a password must contain at least.",
        "auth",
        IsSecurity: true,
        Minimum: 8, Maximum: 256);

    /// <summary>Требовать заглавные буквы.</summary>
    public static readonly SettingKey<bool> RequireUppercase = new("auth.password_require_uppercase",
        true,
        "Require an uppercase letter",
        "",
        "auth",
        IsSecurity: true);

    /// <summary>Требовать строчные буквы.</summary>
    public static readonly SettingKey<bool> RequireLowercase = new("auth.password_require_lowercase",
        true,
        "Require a lowercase letter",
        "",
        "auth",
        IsSecurity: true);

    /// <summary>Требовать цифру.</summary>
    public static readonly SettingKey<bool> RequireDigit = new("auth.password_require_digit",
        true,
        "Require a digit",
        "",
        "auth",
        IsSecurity: true);

    /// <summary>Требовать знак, не букву и не цифру.</summary>
    public static readonly SettingKey<bool> RequireSymbol = new("auth.password_require_symbol",
        true,
        "Require a special character",
        "",
        "auth",
        IsSecurity: true);

    /// <summary>Сколько неудачных входов подряд до блокировки.</summary>
    public static readonly SettingKey<int> LockoutThreshold = new("auth.lockout_threshold",
        10,
        "Failed sign-ins before lockout",
        "",
        "auth",
        IsSecurity: true,
        Minimum: 1, Maximum: 100);

    /// <summary>На сколько блокируется вход, минут.</summary>
    public static readonly SettingKey<int> LockoutMinutes = new("auth.lockout_minutes",
        15,
        "Lockout duration, minutes",
        "",
        "auth",
        IsSecurity: true,
        Minimum: 1, Maximum: 10_080);

    /// <summary>Нарастающая блокировка: каждая следующая подряд — вдвое дольше (решение 0119).</summary>
    public static readonly SettingKey<bool> LockoutEscalation = new("auth.lockout_escalation",
        true,
        "Each next lockout lasts twice as long",
        "",
        "auth",
        IsSecurity: true);

    /// <summary>Потолок нарастающей блокировки, минут.</summary>
    public static readonly SettingKey<int> LockoutMaxMinutes = new("auth.lockout_max_minutes",
        1_440,
        "Longest lockout, minutes",
        "",
        "auth",
        IsSecurity: true,
        Minimum: 1, Maximum: 10_080);

    /// <summary>Сколько действует ссылка приглашения, минут.</summary>
    public static readonly SettingKey<bool> TotpRequired = new("auth.totp_required",
        false,
        "Require two-factor for everyone",
        "",
        "auth",
        IsSecurity: true);

    /// <summary>Требовать второй фактор от суперпользователей.</summary>
    public static readonly SettingKey<bool> TotpRequiredForSuperusers = new("auth.totp_required_for_superusers",
        true,
        "Require two-factor for superusers",
        "",
        "auth",
        IsSecurity: true);

    /// <summary>Чем подтверждают вход по умолчанию.</summary>
    public static readonly SettingKey<string> SecondFactor = new(
        "auth.second_factor",
        Default: "email",
        "Second factor",
        "How sign-in is confirmed by default. There is no off: the factor is required for "
        + "everyone, only the method varies. Someone who has set up an authenticator keeps using "
        + "it unless they pick e-mail themselves.",
        "auth",
        IsSecurity: true,
        Choices: ["totp", "email"]);

    /// <summary>Сколько живёт расписка между вводом пароля и вводом кода.</summary>
    public static readonly SettingKey<int> TwoFactorChallengeMinutes = new("auth.two_factor_challenge_minutes",
        5,
        "Two-factor challenge lifetime, minutes",
        "",
        "auth",
        IsSecurity: true,
        Minimum: 1, Maximum: 60);

    /// <summary>Сколько минут подтверждённый второй фактор открывает опасные действия этой сессии.</summary>
    public static readonly SettingKey<int> StepUpMinutes = new("auth.step_up_minutes",
        Default: 5,
        "Fresh second factor window, minutes",
        "How long a second factor confirmed in a session keeps sensitive actions open in that "
        + "session: user management, trusted clients, your password and second factor. Signing in "
        + "with a code counts as a confirmation; after the window the action asks for a new code.",
        "auth",
        IsSecurity: true,
        Minimum: 1, Maximum: 120);

    /// <summary>Сколько держать мёртвые сессии и погашенные ссылки.</summary>
    public static readonly SettingKey<int> LinkRetentionDays = new("identity.link_retention_days",
        30,
        "Keep spent links, days",
        "",
        "auth",
        Minimum: 1, Maximum: 3650);

    /// <summary>Сколько дней машина считается доверенной.</summary>
    public static readonly SettingKey<int> TrustedDeviceDays = new("auth.trusted_device_days",
        30,
        "Trusted device lifetime, days",
        "For how many days a device stays trusted and skips the second factor. Zero disables trust.",
        "auth",
        IsSecurity: true,
        Minimum: 0, Maximum: 365);

    public static readonly SettingKey<int> InvitationTtlMinutes = new("auth.invitation_ttl_minutes",
        Default: 60,
        "Invitation lifetime, minutes",
        "After how many minutes the invitation link stops working.",
        "auth",
        IsSecurity: true,
        Minimum: 5, Maximum: 43_200);
}
