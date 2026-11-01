using Modules.Core.Contracts.Audit;
using Modules.Core.Data;
using Modules.Core.Errors;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Modules.Audit.Writing;

/// <summary>Пишет событие безопасности своим подключением и будит подписчиков.</summary>
internal sealed class SecurityEventWriter(
    IDbContextFactory<AppDbContext> contexts,
    ChangesRedactor redactor,
    IAuditContextAccessor context,
    IEnumerable<IAuditAlertSink> sinks,
    TimeProvider clock,
    ILogger<SecurityEventWriter> log) : ISecurityEventWriter
{
    public async Task RecordAsync(SecurityEvent securityEvent, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(securityEvent);

        var entry = new AuditEntry(
            securityEvent.Action,
            ActorId: securityEvent.ActorId,
            Result: AuditResult.Deny,
            Reason: securityEvent.Summary,
            Changes: securityEvent.Details);

        try
        {
            await using var db = await contexts.CreateDbContextAsync(cancellationToken);

            db.Add(AuditRow.From(entry, context.Current, clock.GetUtcNow(), redactor));

            // ⚠ С пределом ожидания головы цепи (решение 0110): разбор у OwnConnection.
            await OwnConnection.SaveAsync(db, cancellationToken);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            log.LogError(
                "security_event_write_failed {Action} {Error}",
                securityEvent.Action.Name, SafeError.Describe(error));
        }

        await RaiseAsync(securityEvent, cancellationToken);
    }

    /// <summary>Разбудить подписчиков.</summary>
    private async Task RaiseAsync(SecurityEvent securityEvent, CancellationToken cancellationToken)
    {
        var alert = new AuditAlert(
            securityEvent.Action.Name,
            securityEvent.ActorId,
            securityEvent.Summary,
            securityEvent.Details ?? new Dictionary<string, object?>());

        foreach (var sink in sinks)
        {
            try
            {
                await sink.RaiseAsync(alert, cancellationToken);
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                log.LogError(
                    "security_alert_failed {Action} {Sink} {Error}",
                    securityEvent.Action.Name, sink.GetType().Name, SafeError.Describe(error));
            }
        }
    }
}
