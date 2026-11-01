using Modules.Core.Contracts.Mail;

namespace Modules.Mail.Templates;

/// <summary>Виды писем, которые объявляет сам модуль почты.</summary>
// ⚠ `internal`, а не `public`: открытый тип модуля становится его контрактом.
internal static class MailKinds
{
    /// <summary>Проверочное письмо из настроек почты.</summary>
    public const string Test = "test";
}

/// <summary>Тексты проверочного письма.</summary>
internal sealed class MailOwnTemplates : IMailTemplateProvider
{
    public IReadOnlyList<MailTemplate> Templates { get; } =
    [
        new(
            MailKinds.Test,
            MailLocales.Ru,
            "{product}: проверка почты",
            "Если вы это читаете, отправка писем настроена верно.\n\n"
            + "Письмо отправлено вручную из настроек почты в админке и ничего не значит для\n"
            + "учётных записей."),
        new(
            MailKinds.Test,
            MailLocales.Uk,
            "{product}: перевірка пошти",
            "Якщо ви це читаєте, надсилання листів налаштовано правильно.\n\n"
            + "Лист надіслано вручну з налаштувань пошти в адмінці й нічого не означає для\n"
            + "облікових записів."),
        new(
            MailKinds.Test,
            MailLocales.En,
            "{product}: mail check",
            "If you are reading this, sending mail is configured correctly.\n\n"
            + "This letter was sent by hand from the mail settings in the admin area and means\n"
            + "nothing for any account."),
    ];
}
