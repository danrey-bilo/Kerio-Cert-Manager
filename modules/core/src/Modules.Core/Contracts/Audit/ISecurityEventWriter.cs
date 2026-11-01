namespace Modules.Core.Contracts.Audit;

/// <summary>Событие безопасности, которое обязано пережить судьбу текущего запроса.</summary>
public sealed record SecurityEvent(
    AuditAction Action,
    Guid? ActorId,
    string Summary,
    IReadOnlyDictionary<string, object?>? Details = null);

/// <summary>Запись событий безопасности: <b>своим подключением, немедленно, с тревогой</b>.</summary>
public interface ISecurityEventWriter
{
    /// <summary>Записать событие и поднять тревогу.</summary>
    Task RecordAsync(SecurityEvent securityEvent, CancellationToken cancellationToken = default);
}
