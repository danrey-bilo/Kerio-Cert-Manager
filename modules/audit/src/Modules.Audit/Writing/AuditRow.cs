using Modules.Audit.Domain;
using Modules.Core.Contracts.Audit;

namespace Modules.Audit.Writing;

/// <summary>Превращение события в строку таблицы.</summary>
internal static class AuditRow
{
    /// <summary>Собрать строку журнала.</summary>
    public static AuditLogEntry From(
        AuditEntry entry, AuditContext current, DateTimeOffset now, ChangesRedactor redactor) =>
        new()
        {
            Ts = Truncate(now),

            ActorId = entry.ActorId ?? current.ActorId,

            // ⚠ Три источника, и порядок значим.
            ActorType = Map(entry.ActorType ?? current.ActorType ?? AuditActorType.User),
            Action = entry.Action.Name,
            Result = Map(entry.Result),
            Permission = entry.Permission,
            ResourceType = entry.ResourceType,
            ResourceId = entry.ResourceId,
            Reason = entry.Reason,
            ChangesJson = redactor.ToJson(entry.Changes),
            OccurredAt = entry.OccurredAt is { } occurred ? Truncate(occurred) : null,

            ActorIp = current.ActorIp,
            UserAgent = current.UserAgent,
            RequestId = current.RequestId,
            SessionId = current.SessionId,
        };

    /// <summary>Отбросить всё точнее микросекунды — см.</summary>
    public static DateTimeOffset Truncate(DateTimeOffset value) =>
        new(value.Ticks - value.Ticks % (TimeSpan.TicksPerMillisecond / 1000), value.Offset);

    public static string Map(AuditActorType value) => value switch
    {
        AuditActorType.User => "user",
        AuditActorType.ApiToken => "api_token",
        AuditActorType.System => "system",
        _ => throw new ArgumentOutOfRangeException(nameof(value), value, null),
    };

    public static string Map(AuditResult value) => value switch
    {
        AuditResult.Allow => "allow",
        AuditResult.Deny => "deny",
        _ => throw new ArgumentOutOfRangeException(nameof(value), value, null),
    };
}
