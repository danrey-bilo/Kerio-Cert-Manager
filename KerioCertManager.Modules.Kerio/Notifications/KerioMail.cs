using Modules.Core.Contracts.Mail;

namespace KerioCertManager.Modules.Kerio.Notifications;

/// <summary>Письма модуля Kerio: установка удалась, не удалась, сводка очереди, предупреждение.</summary>
internal sealed class KerioMail : IMailTemplateProvider
{
    public const string Deployed = "kerio_deployed";

    public const string Failed = "kerio_deploy_failed";

    /// <summary>Очередь опустела: одно письмо обо всех её установках вместо письма на каждую.</summary>
    public const string Summary = "kerio_queue_summary";

    /// <summary>Событие сводки в журнале оповещений.</summary>
    public const string SummaryEvent = "queue_summary";

    public const string Warning = "kerio_warning";

    /// <summary>Событие письма с несколькими предупреждениями в журнале оповещений.</summary>
    public const string DigestEvent = "alerts_digest";

    /// <summary>Таблица подробностей — заполнитель <c>{table:details}</c>.</summary>
    public const string TableName = "details";

    private const string Таблица = "{table:" + TableName + "}";

    public IReadOnlyList<MailTemplate> Templates { get; } =
    [
        new(Deployed, MailLocales.Ru, "{title} на {server}",
            "Новый SSL-сертификат установлен на сервер Kerio Connect и подтверждён по TLS.\n\n" + Таблица + "\n\n{message}\n"),
        new(Deployed, MailLocales.Uk, "{title} на {server}",
            "Новий SSL-сертифікат встановлено на сервер Kerio Connect і підтверджено через TLS.\n\n" + Таблица + "\n\n{message}\n"),
        new(Deployed, MailLocales.En, "{title} on {server}",
            "A new SSL certificate has been installed on the Kerio Connect server and confirmed over TLS.\n\n" + Таблица
            + "\n\n{message}\n"),

        new(Failed, MailLocales.Ru, "{title}: {server}",
            "{message}\n\n" + Таблица + "\n\nПодробности по шагам — в разделе «Журнал установок» панели.\n"),
        new(Failed, MailLocales.Uk, "{title}: {server}",
            "{message}\n\n" + Таблица + "\n\nПодробиці по кроках — у розділі «Журнал встановлень» панелі.\n"),
        new(Failed, MailLocales.En, "{title}: {server}",
            "{message}\n\n" + Таблица + "\n\nStep-by-step details are in the Installation log of the panel.\n"),

        new(Summary, MailLocales.Ru, "{title}",
            "{message}\n\n" + Таблица + "\n\nПодробности по шагам — в разделе «Журнал установок» панели.\n"),
        new(Summary, MailLocales.Uk, "{title}",
            "{message}\n\n" + Таблица + "\n\nПодробиці по кроках — у розділі «Журнал встановлень» панелі.\n"),
        new(Summary, MailLocales.En, "{title}",
            "{message}\n\n" + Таблица + "\n\nStep-by-step details are in the Installation log of the panel.\n"),

        new(Warning, MailLocales.Ru, "{title}", "{message}\n\n" + Таблица + "\n"),
        new(Warning, MailLocales.Uk, "{title}", "{message}\n\n" + Таблица + "\n"),
        new(Warning, MailLocales.En, "{title}", "{message}\n\n" + Таблица + "\n"),
    ];

    public IReadOnlyCollection<string> UnthrottledKinds { get; } = [Deployed, Failed, Summary, Warning];

    /// <summary>Заголовки колонок таблицы подробностей.</summary>
    public static IReadOnlyList<string> Columns(string locale) => locale switch
    {
        MailLocales.Ru => ["Параметр", "Значение"],
        MailLocales.Uk => ["Параметр", "Значення"],
        _ => ["Item", "Value"],
    };

    /// <summary>Заголовки колонок сводки очереди.</summary>
    public static IReadOnlyList<string> SummaryColumns(string locale) => locale switch
    {
        MailLocales.Ru => ["Сертификат", "Итог", "Подробности"],
        MailLocales.Uk => ["Сертифікат", "Результат", "Подробиці"],
        _ => ["Certificate", "Result", "Details"],
    };

    /// <summary>Заголовки колонок письма с несколькими предупреждениями.</summary>
    public static IReadOnlyList<string> DigestColumns(string locale) => locale switch
    {
        MailLocales.Ru => ["Условие", "Подробности"],
        MailLocales.Uk => ["Умова", "Подробиці"],
        _ => ["Condition", "Details"],
    };

    /// <summary>Значение строки — фраза, а не данные: только такие идут в перевод (сервер с именем «Журнал» остаётся собой).</summary>
    public static bool IsPhrase(string key) => key is "reason" or "trigger";

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
