using System.Net;
using Modules.Mail.Contracts;
using Modules.Mail.Sending;
using Modules.Mail.Templates;
using Modules.Core.Contracts.Audit;
using Modules.Core.Contracts.Authz;
using Modules.Core.Contracts.Mail;
using Modules.Core.Contracts.Settings;
using Modules.Core.Data;
using Modules.Core.Errors;
using Modules.Core.Features;
using Microsoft.AspNetCore.Mvc;

namespace Modules.Mail.Api;

/// <summary>Проверочное письмо из админки.</summary>
[ApiController]
[Route("api/mail")]
[Feature("mail.test_message")]
internal sealed class MailTestController(
    AppDbContext db,
    IMailSender sender,
    MailTemplates templates,
    ISettingsReader settings,
    IAuditWriter audit) : ControllerBase
{
    /// <summary>Послать проверочное письмо — сразу, минуя очередь.</summary>
    [HttpPost("test")]
    [RequirePermission("settings.security.manage")]
    [RequireRecentSecondFactor]
    public async Task<IActionResult> SendTest(
        [FromBody] MailTestIn body, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(body);

        var кому = body.To;

        if (string.IsNullOrWhiteSpace(кому))
        {
            throw new AppException(
                ErrorCodes.Validation, "Не указано, на какой адрес слать проверочное письмо.");
        }

        var политика = await MailPolicy.ReadAsync(settings, cancellationToken);

        if (!политика.Configured)
        {
            throw new AppException(
                ErrorCodes.MailNotConfigured,
                "Почта не настроена: включите отправку и задайте адрес сервера.",
                HttpStatusCode.Conflict);
        }

        // ⚠ С параметром `Html` (решение 0121) проверочное письмо уходит той же парой частей, что очередь.
        var письмо = templates.Отрисовать(
            MailKinds.Test,
            body.Locale ?? MailLocales.En,
            new Dictionary<string, string>(StringComparer.Ordinal),
            MailTemplates.БезТаблиц);

        try
        {
            // ⚠ Одно письмо — одна сессия: проверочное письмо шлётся руками из админки, и держать соединение между нажатиями было бы соединением без.
            await using var сессия = sender.Open(политика);

            await сессия.SendAsync(new OutgoingMail(кому, письмо.Subject, письмо.Body, Html: письмо.Html), cancellationToken);
        }
        catch (Exception ошибка) when (ошибка is not OperationCanceledException)
        {
            // ⚠ Неудачный зонд тоже пишется — своей транзакцией, до отказа (ревизия 2026-10-26, решение 0118).
            await db.InTransactionAsync(
                async token =>
                {
                    audit.Write(new AuditEntry(
                        MailActions.TestFailed,
                        Result: AuditResult.Deny,
                        Changes: new Dictionary<string, object?>(StringComparer.Ordinal)
                        {
                            ["host"] = политика.Host,
                            ["port"] = политика.Port,
                            ["error"] = SafeError.Describe(ошибка),
                        }));
                    await db.SaveChangesAsync(token);
                },
                CancellationToken.None);

            // ⚠ Текст чужого сервера уезжает администратору целиком.
            throw new AppException(
                MailErrorCodes.MailSendFailed,
                "Письмо не отправлено: сервер отказал.",
                HttpStatusCode.Conflict,
                new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    // ⚠ Сырой текст — только у отказов транспорта (ревизия 2026-10-26, решение 0118).
                    ["reason"] = ОтказТранспорта(ошибка) ? ошибка.Message : SafeError.Describe(ошибка),
                });
        }

        await db.InTransactionAsync(
            async token =>
            {
                audit.Write(new AuditEntry(
                    MailActions.TestSent,
                    Changes: new Dictionary<string, object?>(StringComparer.Ordinal)
                    {
                        ["host"] = политика.Host,
                        ["port"] = политика.Port,
                    }));
                await db.SaveChangesAsync(token);
            },
            cancellationToken);

        return NoContent();
    }

    /// <summary>Отказ чужого сервера или сети — его текст и есть ответ администратору.</summary>
    private static bool ОтказТранспорта(Exception ошибка) =>
        ошибка is MailConfigurationException
            or MailKit.Net.Smtp.SmtpCommandException
            or MailKit.Net.Smtp.SmtpProtocolException
            or MailKit.Security.AuthenticationException
            or MailKit.Security.SslHandshakeException
            or System.Net.Sockets.SocketException
            or IOException
            or MimeKit.ParseException
            or NotSupportedException
            or PlaintextAuthRefusedException;
}
