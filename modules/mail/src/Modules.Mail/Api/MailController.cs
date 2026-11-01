using System.Text.Json.Serialization;
using Modules.Mail.Domain;
using Modules.Mail.Secrets;
using Modules.Mail.Sending;
using Modules.Core.Contracts.Authz;
using Modules.Core.Contracts.Settings;
using Modules.Core.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Modules.Mail.Api;

/// <summary>Состояние почты для экрана настроек.</summary>
internal sealed record MailStatusOut(
    [property: JsonPropertyName("configured")] bool Configured,
    [property: JsonPropertyName("password_set")] bool PasswordSet,
    [property: JsonPropertyName("pending")] int Pending,
    [property: JsonPropertyName("failed")] int Failed,
    [property: JsonPropertyName("last_error")] string? LastError);

/// <summary>Новый пароль SMTP.</summary>
internal sealed record MailPasswordIn(
    [property: JsonPropertyName("password")] string? Password)
{
    /// <summary>Печать без секретов: запись попадает в лог, в отладчик и в текст исключения.</summary>
    private bool PrintMembers(System.Text.StringBuilder builder)
    {
        builder.Append("секреты скрыты: Password");

        return true;
    }
}

/// <summary>Кому послать проверочное письмо.</summary>
internal sealed record MailTestIn(
    [property: JsonPropertyName("to")] string? To,
    [property: JsonPropertyName("locale")] string? Locale);

/// <summary>Почта установки: состояние, пароль, проверка.</summary>
[ApiController]
[Route("api/mail")]
internal sealed class MailController(
    AppDbContext db,
    SystemSecrets secrets,
    ISettingsReader settings,
    MailModuleOptions? options = null) : ControllerBase
{
    /// <summary>Состояние почты: настроена ли, задан ли пароль, что в очереди.</summary>
    [HttpGet]
    [RequirePermission("settings.read")]
    public async Task<MailStatusOut> Status(CancellationToken cancellationToken)
    {
        var политика = await MailPolicy.ReadAsync(settings, cancellationToken);

        // ⚠ Истёкшее не ждёт отправки (решение 0117): насос его не берёт, и в счёте «ждут» оно выглядело бы застрявшей очередью.
        var сейчас = DateTimeOffset.UtcNow;
        var сдалось = MailDelivery.Сдалось;
        var ждут = await db.Set<MailOutbox>()
            .CountAsync(
                п => п.SentAt == null && п.Attempts < политика.MaxAttempts && п.NextAttemptAt < сдалось
                    && (п.ExpiresAt == null || п.ExpiresAt > сейчас),
                cancellationToken);

        var сдались = await db.Set<MailOutbox>()
            .CountAsync(
                п => п.SentAt == null && (п.Attempts >= политика.MaxAttempts || п.NextAttemptAt >= сдалось),
                cancellationToken);

        // ⚠ Последняя ошибка — самого свежего неотправленного.
        var последняя = await db.Set<MailOutbox>()
            .Where(п => п.SentAt == null && п.LastError != null)
            .OrderByDescending(п => п.CreatedAt)
            .Select(п => п.LastError)
            .FirstOrDefaultAsync(cancellationToken);

        // ⚠ Пароль, переданный проектом, — тоже «задан» (решение 0112), но значение наружу не уходит ни в каком виде, как и у хранимого.
        var парольЗадан = options?.Features.StoredPassword == false
            ? options.SmtpPassword is { Length: > 0 }
            : await secrets.HasAsync(SystemSecretNames.SmtpPassword, cancellationToken);

        return new MailStatusOut(политика.Configured, парольЗадан, ждут, сдались, последняя);
    }

}
