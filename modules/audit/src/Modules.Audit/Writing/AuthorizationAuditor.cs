using Modules.Audit.Contracts;
using Modules.Audit.Settings;
using Modules.Core.Contracts.Audit;
using Modules.Core.Contracts.Settings;
using Modules.Core.Data;
using Modules.Core.Errors;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Modules.Audit.Writing;

/// <summary>Запись решений о правах.</summary>
internal sealed class AuthorizationAuditor(
    IDbContextFactory<AppDbContext> contexts,
    ChangesRedactor redactor,
    IAuditContextAccessor context,
    ISettingsReader settings,
    TimeProvider clock,
    SuperuserReadRepeats repeats,
    ILogger<AuthorizationAuditor> log) : IAuthorizationAuditor
{
    public async Task RecordAsync(
        Guid? actorId,
        AuditDecision decision,
        string? resourceType,
        Guid? resourceId,
        CancellationToken cancellationToken = default)
    {
        if (!await ShouldRecordAsync(decision, cancellationToken))
        {
            return;
        }

        // ⚠ Повтор чтения суперпользователя в той же сессии — не пишется (решение 0096).
        var повтор = decision is { Bypass: true, ReadOnly: true }
            && (context.Current.SessionId ?? actorId) is { } кто
                ? кто
                : (Guid?)null;

        if (повтор is { } ключ && repeats.Записано(ключ, decision.Permission))
        {
            return;
        }

        var entry = new AuditEntry(
            AuditActions.PermissionCheck,
            ActorId: actorId,
            Result: decision.Allow ? AuditResult.Allow : AuditResult.Deny,
            Permission: decision.Permission,
            ResourceType: resourceType,
            ResourceId: resourceId,
            Reason: decision.Reason,
            Changes: decision.Bypass ? new Dictionary<string, object?> { ["bypass"] = true } : null);

        try
        {
            await using var db = await contexts.CreateDbContextAsync(cancellationToken);

            db.Add(AuditRow.From(entry, context.Current, clock.GetUtcNow(), redactor));

            // ⚠ С пределом ожидания головы цепи (решение 0110): разбор у OwnConnection.
            await OwnConnection.SaveAsync(db, cancellationToken);

            if (повтор is { } записанный)
            {
                repeats.Отметить(записанный, decision.Permission);
            }
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            log.LogError(
                "audit_decision_write_failed {Permission} {Result} {Error}",
                decision.Permission, decision.Allow ? "allow" : "deny", SafeError.Describe(error));
        }
    }

    /// <summary>Писать ли эту проверку.</summary>
    private async ValueTask<bool> ShouldRecordAsync(
        AuditDecision decision, CancellationToken cancellationToken) =>
        !decision.Allow
        || decision.Bypass
        || await settings.GetAsync(AuditSettings.LogPermissionAllows, cancellationToken);
}
