using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Modules.Audit.Reading;

/// <summary>Строка журнала, как её видит клиент.</summary>
internal sealed record AuditEntryOut(
    [property: JsonPropertyName("id")] long Id,
    [property: JsonPropertyName("ts")] DateTimeOffset Ts,
    [property: JsonPropertyName("actor_id")] Guid? ActorId,
    [property: JsonPropertyName("actor_type")] string ActorType,
    [property: JsonPropertyName("actor_ip")] string? ActorIp,
    [property: JsonPropertyName("user_agent")] string? UserAgent,
    [property: JsonPropertyName("permission")] string? Permission,
    [property: JsonPropertyName("action")] string Action,
    [property: JsonPropertyName("module")] string? Module,
    [property: JsonPropertyName("resource_type")] string? ResourceType,
    [property: JsonPropertyName("resource_id")] Guid? ResourceId,
    [property: JsonPropertyName("resource_name")] string? ResourceName,
    [property: JsonPropertyName("result")] string Result,
    [property: JsonPropertyName("reason")] string? Reason,
    [property: JsonPropertyName("changes")] JsonNode? Changes,
    [property: JsonPropertyName("request_id")] string? RequestId,
    [property: JsonPropertyName("session_id")] Guid? SessionId,
    [property: JsonPropertyName("occurred_at")] DateTimeOffset? OccurredAt);

/// <summary>Одна страница журнала.</summary>
internal sealed record AuditPageOut(
    [property: JsonPropertyName("items")] IReadOnlyList<AuditEntryOut> Items,
    [property: JsonPropertyName("next_cursor")] string? NextCursor);

/// <summary>Группа событий для фильтра.</summary>
internal sealed record AuditActionGroupOut(
    [property: JsonPropertyName("key")] string Key,
    [property: JsonPropertyName("title")] string? Title,
    [property: JsonPropertyName("actions")] IReadOnlyList<string> Actions);

/// <summary>Справочник событий.</summary>
internal sealed record AuditActionsOut(
    [property: JsonPropertyName("groups")] IReadOnlyList<AuditActionGroupOut> Groups,
    [property: JsonPropertyName("titles")] IReadOnlyDictionary<string, string> Titles);
