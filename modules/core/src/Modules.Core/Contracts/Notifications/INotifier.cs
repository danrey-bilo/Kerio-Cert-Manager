namespace Modules.Core.Contracts.Notifications;

/// <summary>О чём сказать и кому.</summary>
public sealed record NotificationRequest(
    IReadOnlyCollection<Guid> Recipients,
    string Kind,
    IReadOnlyDictionary<string, object?> Payload,
    string? DedupeKey = null,
    DateTimeOffset? DedupeSince = null)
{
    /// <summary>Одному человеку — самый частый случай.</summary>
    public NotificationRequest(
        Guid recipient,
        string kind,
        IReadOnlyDictionary<string, object?> payload,
        string? dedupeKey = null,
        DateTimeOffset? dedupeSince = null)
        : this([recipient], kind, payload, dedupeKey, dedupeSince)
    {
    }
}

/// <summary>Сказать человеку о том, что его касается.</summary>
public interface INotifier
{
    /// <summary>Поставить уведомление в текущую единицу работы.</summary>
    Task<bool> NotifyAsync(NotificationRequest request, CancellationToken cancellationToken = default);
}
