using Modules.Core.Contracts.Settings;

namespace Modules.Mail.Settings;

/// <summary>Настройки почты и внешнего адреса установки.</summary>
internal static class MailSettings
{
    /// <summary>Раздел на экране настроек.</summary>
    public const string Category = "mail";

    /// <summary>Слать ли почту вообще.</summary>
    public static readonly SettingKey<bool> Enabled = new(
        "mail.enabled",
        Default: false,
        "Send mail",
        "Off by default: until an administrator fills in the server, nothing is sent and "
        + "password reset stays unavailable. Turning it off does not delete the settings.",
        Category,
        IsSecurity: true);

    /// <summary>Сервер исходящей почты.</summary>
    public static readonly SettingKey<string?> SmtpHost = new(
        "mail.smtp_host",
        Default: null,
        "SMTP host",
        "Host name of the outgoing mail server.",
        Category,
        IsSecurity: true);

    /// <summary>Порт.</summary>
    public static readonly SettingKey<int> SmtpPort = new(
        "mail.smtp_port",
        Default: 587,
        "SMTP port",
        "587 for STARTTLS, 465 for implicit TLS, 25 for a relay on the same network.",
        Category,
        IsSecurity: true,
        Minimum: 1,
        Maximum: 65535);

    /// <summary>Как защищено соединение.</summary>
    public static readonly SettingKey<string> SmtpSecurity = new(
        "mail.smtp_security",
        Default: "starttls",
        "Connection security",
        "Set explicitly rather than guessed from the port: 587 exists without TLS too, and "
        + "silently downgrading protection is the worst kind of compatibility.",
        Category,
        IsSecurity: true,
        Choices: ["starttls", "tls", "none"]);

    /// <summary>Учётная запись SMTP.</summary>
    public static readonly SettingKey<string?> SmtpUsername = new(
        "mail.smtp_username",
        Default: null,
        "SMTP user",
        "Leave empty for a relay that does not ask for credentials.",
        Category,
        IsSecurity: true);

    /// <summary>Адрес отправителя в заголовке From.</summary>
    public static readonly SettingKey<string?> FromAddress = new(
        "mail.from_address",
        Default: null,
        "From address",
        "Address in the From header. Not the same thing as the SMTP user: with most providers "
        + "the user name is not an address at all.",
        Category,
        IsSecurity: true);

    /// <summary>Имя отправителя в заголовке From.</summary>
    public static readonly SettingKey<string> FromName = new(
        "mail.from_name",
        Default: "",
        "From name",
        "Display name in the From header.",
        Category,
        IsSecurity: true);

    /// <summary>Сколько писем в час на один адрес и вид.</summary>
    public static readonly SettingKey<int> RatePerHour = new(
        "mail.rate_per_hour",
        Default: 5,
        "Letters per hour per address",
        "Counted per address and per kind. Protects the mailbox of someone whose address "
        + "an attacker keeps feeding to the reset form.",
        Category,
        IsSecurity: true,
        Minimum: 1,
        Maximum: 100);

    /// <summary>Сколько раз пробовать доставить.</summary>
    public static readonly SettingKey<int> MaxAttempts = new(
        "mail.max_attempts",
        Default: 5,
        "Delivery attempts",
        "After this many failures the letter is given up on and its error is left for the "
        + "administrator to read.",
        Category,
        IsSecurity: true,
        Minimum: 1,
        Maximum: 50);

    /// <summary>Внешний адрес установки — из него собираются ссылки в письмах.</summary>
    public static readonly SettingKey<string?> PublicUrl = new(
        "server.public_url",
        Default: null,
        "Public URL",
        "The address the installation is reachable at from outside. Links in letters are built "
        + "from it; empty means links cannot be built.",
        "server",
        IsSecurity: true,
        Nullable: true);
}

/// <summary>Объявление настроек почты для общего экрана настроек.</summary>
internal sealed class MailSettingDeclarations : ISettingDeclarationProvider
{
    public IReadOnlyList<ISettingDeclaration> Declarations { get; } =
    [
        MailSettings.Enabled,
        MailSettings.SmtpHost,
        MailSettings.SmtpPort,
        MailSettings.SmtpSecurity,
        MailSettings.SmtpUsername,
        MailSettings.FromName,
        MailSettings.FromAddress,
        MailSettings.RatePerHour,
        MailSettings.MaxAttempts,
        MailSettings.PublicUrl,
    ];
}
