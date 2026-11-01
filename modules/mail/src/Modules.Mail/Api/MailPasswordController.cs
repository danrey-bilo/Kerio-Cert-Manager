using Modules.Mail.Contracts;
using Modules.Mail.Secrets;
using Modules.Mail.Sending;
using Modules.Core.Contracts.Audit;
using Modules.Core.Contracts.Settings;
using Modules.Core.Contracts.Authz;
using Modules.Core.Data;
using Microsoft.AspNetCore.Mvc;

namespace Modules.Mail.Api;

/// <summary>Пароль SMTP, хранимый в базе: задать или снять.</summary>
[ApiController]
[Route("api/mail")]
[Modules.Core.Features.Feature("mail.stored_password")]
internal sealed class MailPasswordController(
    AppDbContext db,
    SystemSecrets secrets,
    IAuditWriter audit,
    ISettingsReader settings,
    MailModuleOptions? options = null) : ControllerBase
{
    /// <summary>Задать или снять пароль SMTP.</summary>
    [HttpPut("password")]
    [RequirePermission("settings.security.manage")]
    [RequireRecentSecondFactor]
    public async Task<IActionResult> SetPassword(
        [FromBody] MailPasswordIn body, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(body);

        var цель = options?.BindSmtpPassword == true
            ? SmtpSender.Цель(await MailPolicy.ReadAsync(settings, cancellationToken))
            : null;

        await db.InTransactionAsync(
            async token =>
            {
                var задан = !string.IsNullOrEmpty(body.Password);

                if (задан && цель is not null)
                {
                    await secrets.PutAsync(SystemSecretNames.SmtpPassword, body.Password!, цель, token);
                }
                else if (задан)
                {
                    await secrets.PutAsync(SystemSecretNames.SmtpPassword, body.Password!, token);
                }
                else
                {
                    await secrets.DropAsync(SystemSecretNames.SmtpPassword, token);
                }

                audit.Write(new AuditEntry(
                    MailActions.PasswordSet,
                    Changes: new Dictionary<string, object?>(StringComparer.Ordinal)
                    {
                        ["set"] = задан,
                    }));

                await db.SaveChangesAsync(token);
            },
            cancellationToken);

        return NoContent();
    }
}
