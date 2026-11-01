namespace Modules.Core.Contracts.Audit;

/// <summary>Решение движка прав — то, что записывается в журнал.</summary>
public sealed record AuditDecision(
    bool Allow,
    string Permission,
    string Reason,
    bool Bypass = false,
    bool ReadOnly = false);

/// <summary>Запись решений о правах — отдельно от обычных событий.</summary>
public interface IAuthorizationAuditor
{
    /// <summary>Записать решение о доступе.</summary>
    Task RecordAsync(
        Guid? actorId,
        AuditDecision decision,
        string? resourceType,
        Guid? resourceId,
        CancellationToken cancellationToken = default);
}
