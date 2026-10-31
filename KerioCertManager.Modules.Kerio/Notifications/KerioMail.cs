using Modules.Core.Contracts.Mail;

namespace KerioCertManager.Modules.Kerio.Notifications;

/// <summary>Письма модуля Kerio: установка удалась, не удалась, предупреждение.</summary>
internal sealed class KerioMail : IMailTemplateProvider
{
    public const string Deployed = "kerio_deployed";

    public const string Failed = "kerio_deploy_failed";

    public const string Warning = "kerio_warning";

    /// <summary>Таблица подробностей — заполнитель <c>{table:details}</c>.</summary>
    public const string TableName = "details";

    private const string Таблица = "{table:" + TableName + "}";

    public IReadOnlyList<MailTemplate> Templates { get; } =
    [
        new(Deployed, MailLocales.Ru, "Сертификат {common_name} установлен на {server}",
            "Новый SSL-сертификат установлен на сервер Kerio Connect и подтверждён по TLS.\n\n" + Таблица + "\n\n{message}\n"),
        new(Deployed, MailLocales.Uk, "Сертифікат {common_name} встановлено на {server}",
            "Новий SSL-сертифікат встановлено на сервер Kerio Connect і підтверджено через TLS.\n\n" + Таблица + "\n\n{message}\n"),
        new(Deployed, MailLocales.En, "Certificate {common_name} installed on {server}",
            "A new SSL certificate has been installed on the Kerio Connect server and confirmed over TLS.\n\n" + Таблица
            + "\n\n{message}\n"),

        new(Failed, MailLocales.Ru, "{title}: {server}",
            "{message}\n\n" + Таблица + "\n\nПодробности по шагам — в разделе «Журнал установок» панели.\n"),
        new(Failed, MailLocales.Uk, "{title}: {server}",
            "{message}\n\n" + Таблица + "\n\nПодробиці по кроках — у розділі «Журнал встановлень» панелі.\n"),
        new(Failed, MailLocales.En, "{title}: {server}",
            "{message}\n\n" + Таблица + "\n\nStep-by-step details are in the Installations log of the panel.\n"),

        new(Warning, MailLocales.Ru, "{title}", "{message}\n\n" + Таблица + "\n"),
        new(Warning, MailLocales.Uk, "{title}", "{message}\n\n" + Таблица + "\n"),
        new(Warning, MailLocales.En, "{title}", "{message}\n\n" + Таблица + "\n"),
    ];

    public IReadOnlyCollection<string> UnthrottledKinds { get; } = [Deployed, Failed, Warning];

    /// <summary>Заголовки колонок таблицы подробностей.</summary>
    public static IReadOnlyList<string> Columns(string locale) => locale switch
    {
        MailLocales.Ru => ["Параметр", "Значение"],
        MailLocales.Uk => ["Параметр", "Значення"],
        _ => ["Item", "Value"],
    };

    /// <summary>Подписи строк таблицы на языке письма.</summary>
    public static string Label(string key, string locale) => (key, locale) switch
    {
        ("server", MailLocales.Ru) => "Сервер",
        ("server", MailLocales.Uk) => "Сервер",
        ("server", _) => "Server",
        ("address", MailLocales.Ru) => "Адрес",
        ("address", MailLocales.Uk) => "Адреса",
        ("address", _) => "Address",
        ("certificate", MailLocales.Ru) => "Сертификат",
        ("certificate", MailLocales.Uk) => "Сертифікат",
        ("certificate", _) => "Certificate",
        ("serial", MailLocales.Ru) => "Серийный номер",
        ("serial", MailLocales.Uk) => "Серійний номер",
        ("serial", _) => "Serial number",
        ("valid_until", MailLocales.Ru) => "Действует до",
        ("valid_until", MailLocales.Uk) => "Дійсний до",
        ("valid_until", _) => "Valid until",
        ("fingerprint", MailLocales.Ru) => "SHA-256",
        ("fingerprint", MailLocales.Uk) => "SHA-256",
        ("fingerprint", _) => "SHA-256",
        ("step", MailLocales.Ru) => "Шаг",
        ("step", MailLocales.Uk) => "Крок",
        ("step", _) => "Step",
        ("reason", MailLocales.Ru) => "Причина",
        ("reason", MailLocales.Uk) => "Причина",
        ("reason", _) => "Reason",
        ("next", MailLocales.Ru) => "Следующая попытка",
        ("next", MailLocales.Uk) => "Наступна спроба",
        ("next", _) => "Next attempt",
        ("trigger", MailLocales.Ru) => "Причина установки",
        ("trigger", MailLocales.Uk) => "Причина встановлення",
        ("trigger", _) => "Why installed",
        ("served", MailLocales.Ru) => "Сейчас на сервере",
        ("served", MailLocales.Uk) => "Зараз на сервері",
        ("served", _) => "Presented now",
        _ => key,
    };
}
