using Modules.Core.Contracts.Notifications;

namespace Modules.Tokens.Notifications;

/// <summary>События сессий, о которых человеку говорят лично, а не только в журнале.</summary>
[Modules.Core.Features.Feature("tokens.session_notices")]
internal sealed class SessionNotices(IEnumerable<INotifier> notifiers)
{
    /// <summary>Вход с незнакомого устройства.</summary>
    public Task UnknownDeviceAsync(
        Guid userId, string? name, CancellationToken cancellationToken) =>
        СказатьAsync(
            userId,
            NotificationKinds.UnknownDevice,
            new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                // ⚠ Имя агента — открытое поле, не секрет; адреса здесь НЕТ намеренно: он персональные данные и в ленте не нужен, а в журнале он уже есть.
                ["device"] = name,
                ["summary"] = "Вход с незнакомого устройства",
            },
            $"{NotificationKinds.UnknownDevice}:{name}",
            cancellationToken);

    /// <summary>Предъявлен уже потраченный токен обновления.</summary>
    public Task TokenReuseAsync(Guid userId, CancellationToken cancellationToken) =>
        СказатьAsync(
            userId,
            NotificationKinds.TokenReuse,
            new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["summary"] = "Сессия закрыта: предъявлен уже использованный токен",
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

/// <summary>Виды уведомлений, которые заводит модуль токенов.</summary>
internal static class NotificationKinds
{
    /// <summary>Вход с устройства, которого раньше не видели.</summary>
    public const string UnknownDevice = "security.unknown_device";

    /// <summary>Предъявлен уже потраченный токен обновления.</summary>
    public const string TokenReuse = "security.token_reuse";
}
