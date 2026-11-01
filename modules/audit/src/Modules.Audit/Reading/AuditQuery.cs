using Modules.Core.Contracts.Audit;

namespace Modules.Audit.Reading;

/// <summary>Чем сужают выдачу журнала.</summary>
internal sealed record AuditQuery(
    Guid? ActorId = null,
    string? Action = null,
    IReadOnlyList<string>? Actions = null,
    string? ResourceType = null,
    Guid? ResourceId = null,
    AuditResult? Result = null,
    DateTimeOffset? Since = null,
    DateTimeOffset? Until = null,
    string? ActorIp = null,
    Guid? SessionId = null,
    string? RequestId = null);
