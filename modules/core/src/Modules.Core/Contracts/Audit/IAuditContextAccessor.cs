using System.Net;

namespace Modules.Core.Contracts.Audit;

/// <summary>Что известно о запросе, в рамках которого пишется событие.</summary>
public sealed record AuditContext(
    string? RequestId = null,
    IPAddress? ActorIp = null,
    string? UserAgent = null,
    Guid? ActorId = null,
    Guid? SessionId = null,
    AuditActorType? ActorType = null);

/// <summary>Держатель контекста запроса на время его обработки.</summary>
public interface IAuditContextAccessor
{
    /// <summary>Что известно сейчас.</summary>
    AuditContext Current { get; }

    /// <summary>Дополнить известное.</summary>
    void Bind(AuditContext patch);
}
