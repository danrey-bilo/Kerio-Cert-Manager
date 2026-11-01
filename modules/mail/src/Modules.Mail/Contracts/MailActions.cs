using Modules.Core.Contracts.Audit;

namespace Modules.Mail.Contracts;

/// <summary>События журнала, которые пишет модуль почты.</summary>
public static class MailActions
{
    /// <summary>Задан или убран пароль SMTP.</summary>
    public static readonly AuditAction PasswordSet = new("mail.password_set", "Задан пароль SMTP");

    /// <summary>Послано проверочное письмо.</summary>
    public static readonly AuditAction TestSent = new("mail.test_sent", "Послано проверочное письмо");

    /// <summary>Проверочное письмо не отправлено.</summary>
    public static readonly AuditAction TestFailed = new("mail.test_failed", "Проверочное письмо не отправлено");
}
