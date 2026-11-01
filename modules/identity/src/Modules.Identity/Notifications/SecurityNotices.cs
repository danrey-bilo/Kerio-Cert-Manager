using Modules.Core.Contracts.Notifications;

namespace Modules.Identity.Notifications;

/// <summary>События безопасности, о которых человеку говорят лично, а не только в журнале.</summary>
internal sealed class SecurityNotices(IEnumerable<INotifier> notifiers)
{
    /// <summary>Учётная запись заблокирована перебором.</summary>
    public Task LockedOutAsync(Guid userId, string reason, CancellationToken cancellationToken) =>
        СказатьAsync(
            userId,
            NotificationKinds.AccountLocked,
            new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["reason"] = reason,
                ["summary"] = "Вход заблокирован после неудачных попыток",
            },
            NotificationKinds.AccountLocked,
            cancellationToken);

    /// <summary>Пароль сброшен по ссылке.</summary>
    public Task PasswordResetAsync(Guid userId, CancellationToken cancellationToken) =>
        СказатьAsync(
            userId,
            NotificationKinds.PasswordReset,
            new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["summary"] = "Пароль изменён по ссылке сброса",
            },
            dedupeKey: null,
            cancellationToken);

    private async Task СказатьAsync(
        Guid userId,
        string kind,
        Dictionary<string, object?> payload,
        string? dedupeKey,
        CancellationToken cancellationToken)
    {
        foreach (var уведомитель in notifiers)
        {
            try
            {
                await уведомитель.NotifyAsync(
                    new NotificationRequest([userId], kind, payload, dedupeKey), cancellationToken);
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                _ = error;
            }
        }
    }
}

/// <summary>Виды уведомлений, которые заводит модуль личностей.</summary>
internal static class NotificationKinds
{
    /// <summary>Вход заблокирован после неудачных попыток.</summary>
    public const string AccountLocked = "security.account_locked";

    /// <summary>Вход по коду восстановления.</summary>
    public const string RecoveryCodeUsed = "security.recovery_code_used";

    /// <summary>Пароль изменён по ссылке сброса.</summary>
    public const string PasswordReset = "security.password_reset";
}
