using Modules.Core.Redaction;

namespace Modules.Mail.Secrets;

/// <summary>Имена, которые модуль объявляет секретными.</summary>
internal sealed class MailSensitiveKeys : ISensitiveKeyProvider
{
    public IReadOnlyCollection<string> Keys { get; } = ["smtp_password", "sealed", "data_key"];
}
