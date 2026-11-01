using Modules.Core.Contracts.Mail;

namespace Modules.Mail.Templates;

/// <summary>Рамка письма: приветствие, суть, благодарность и название продукта.</summary>
internal static class MailFrame
{
    /// <summary>Обернуть тело письма рамкой на языке шаблона.</summary>
    public static string Wrap(string body, string locale, string product)
    {
        ArgumentNullException.ThrowIfNull(body);
        ArgumentNullException.ThrowIfNull(product);

        var (привет, спасибо) = Слова(locale);

        return $"{привет}\n\n{body.Trim('\n')}\n\n{спасибо}\n{product}\n";
    }

    /// <summary>Приветствие и благодарность рамки на языке шаблона.</summary>
    internal static (string Привет, string Спасибо) Слова(string locale) => locale switch
    {
        MailLocales.Ru => ("Здравствуйте!", "Спасибо,"),
        MailLocales.Uk => ("Вітаємо!", "Дякуємо,"),
        _ => ("Hi,", "Thank You,"),
    };
}
