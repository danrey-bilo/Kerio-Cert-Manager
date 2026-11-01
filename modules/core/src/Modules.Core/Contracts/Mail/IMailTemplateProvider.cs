namespace Modules.Core.Contracts.Mail;

/// <summary>Языки, на которых установка умеет писать.</summary>
public static class MailLocales
{
    /// <summary>Украинский.</summary>
    public const string Uk = "uk";

    /// <summary>Русский.</summary>
    public const string Ru = "ru";

    /// <summary>Английский.</summary>
    public const string En = "en";

    /// <summary>Все три, в порядке справочника.</summary>
    public static readonly IReadOnlyList<string> All = [Uk, Ru, En];
}

/// <summary>Один текст письма: вид, язык, тема, тело.</summary>
public sealed record MailTemplate(string Kind, string Locale, string Subject, string Body);

/// <summary>Модуль объявляет свои письма.</summary>
public interface IMailTemplateProvider
{
    /// <summary>Письма этого модуля: по одному на пару «вид + язык».</summary>
    IReadOnlyList<MailTemplate> Templates { get; }

    /// <summary>Виды этого модуля, на которые не действует предел <c>mail.rate_per_hour</c>.</summary>
    IReadOnlyCollection<string> UnthrottledKinds => [];
}
