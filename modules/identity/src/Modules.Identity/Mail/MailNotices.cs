using System.Globalization;
using Modules.Identity.Domain;
using Modules.Core.Contracts.Mail;
using Modules.Core.Contracts.Settings;
using Microsoft.Extensions.Logging;

namespace Modules.Identity.Mail;

/// <summary>Письма, которые ставит модуль личностей.</summary>
internal sealed class MailNotices(
    IdentityMailGate mail,
    IPublicOrigin origin,
    ILogger<MailNotices> log)
{
    /// <summary>Поставить приглашение.</summary>
    public Task<MailQueueResult> InvitationAsync(
        User user, string token, DateTimeOffset expiresAt, CancellationToken cancellationToken = default) =>
        ПоставитьAsync(user, IdentityMailKinds.Invitation, "activate", token, expiresAt, cancellationToken);

    /// <summary>Поставить письмо сброса пароля.</summary>
    public Task<MailQueueResult> PasswordResetAsync(
        User user, string token, DateTimeOffset expiresAt, CancellationToken cancellationToken = default) =>
        ПоставитьAsync(user, IdentityMailKinds.PasswordReset, "password-reset", token, expiresAt, cancellationToken);

    /// <summary>Поставить письмо «доступ приостановлен».</summary>
    public async Task<bool> AccountSuspendedAsync(User user, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(user);

        return await ПоставитьПисьмоAsync(
            user,
            IdentityMailKinds.AccountSuspended,
            new Dictionary<string, string>(StringComparer.Ordinal),
            cancellationToken) == MailQueueResult.Queued;
    }

    private async Task<MailQueueResult> ПоставитьAsync(
        User user,
        string kind,
        string route,
        string token,
        DateTimeOffset expiresAt,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(user);

        var адрес = await origin.ConfiguredAsync(cancellationToken);

        if (адрес is null)
        {
            // ⚠ Громко, но без адреса получателя и без токена: строка уезжает в общий лог.
            log.LogWarning("mail_link_origin_missing {Kind}", kind);
            return MailQueueResult.NotConfigured;
        }

        var минут = (int)Math.Max(1, Math.Round((expiresAt - DateTimeOffset.UtcNow).TotalMinutes));

        return await ПоставитьПисьмоAsync(
            user,
            kind,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["link"] = $"{адрес}/{route}#token={Uri.EscapeDataString(token)}",
                ["minutes"] = минут.ToString(CultureInfo.InvariantCulture),
            },
            cancellationToken,

            // ⚠ Срок ссылки — срок письма (ревизия 2026-10-26, решение 0118).
            expiresAt);
    }

    private async Task<MailQueueResult> ПоставитьПисьмоAsync(
        User user,
        string kind,
        IReadOnlyDictionary<string, string> values,
        CancellationToken cancellationToken,
        DateTimeOffset? expiresAt = null)
    {
        var язык = await mail.LocaleAsync(user.Id, cancellationToken);

        var исход = await mail.EnqueueAsync(user.Email, kind, язык, values, expiresAt, cancellationToken);

        if (исход != MailQueueResult.Queued)
        {
            // ⚠ Вид письма есть, адреса нет: сам факт «на этот адрес просили сброс» является сведением о человеке (правило 14).
            log.LogInformation("mail_not_queued {Kind} {Reason}", kind, исход);
        }

        return исход;
    }
}
