using Modules.Core.Contracts.Audit;

namespace KerioCertManager.Api.Infrastructure;

/// <summary>Событие — в журнал модуля audit и строкой «audit …» в лог.</summary>
internal sealed class LoggedAuditWriter(IAuditWriter journal, LogAuditWriter log) : IAuditWriter
{
    public void Write(AuditEntry entry)
    {
        log.Write(entry);
        journal.Write(entry);
    }
}

/// <summary>Событие безопасности — в журнал модуля audit и строкой в лог.</summary>
internal sealed class LoggedSecurityEventWriter(ISecurityEventWriter journal, LogAuditWriter log) : ISecurityEventWriter
{
    public async Task RecordAsync(SecurityEvent securityEvent, CancellationToken cancellationToken = default)
    {
        await log.RecordAsync(securityEvent, cancellationToken);
        await journal.RecordAsync(securityEvent, cancellationToken);
    }
}

/// <summary>Решение о праве — в журнал модуля audit и отказ строкой в лог.</summary>
internal sealed class LoggedAuthorizationAuditor(IAuthorizationAuditor journal, LogAuditWriter log) : IAuthorizationAuditor
{
    public async Task RecordAsync(
        Guid? actorId,
        AuditDecision decision,
        string? resourceType,
        Guid? resourceId,
        CancellationToken cancellationToken = default)
    {
        await log.RecordAsync(actorId, decision, resourceType, resourceId, cancellationToken);
        await journal.RecordAsync(actorId, decision, resourceType, resourceId, cancellationToken);
    }
}
