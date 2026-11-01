using Modules.Audit.Catalog;
using Modules.Audit.Domain;
using Modules.Core.Contracts.Audit;
using Modules.Core.Data;
using Microsoft.Extensions.Logging;

namespace Modules.Audit.Writing;

/// <summary>Запись события в транзакцию вызывающего.</summary>
internal sealed class AuditWriter(
    AppDbContext db,
    ChangesRedactor redactor,
    AuditActionCatalog catalog,
    IAuditContextAccessor context,
    TimeProvider clock,
    AuditModuleOptions options,
    ILogger<AuditWriter> log) : IAuditWriter
{
    public void Write(AuditEntry entry)
    {
        if (!catalog.Knows(entry.Action))
        {
            if (options.StrictActionCatalog)
            {
                throw new InvalidOperationException(
                    $"Событие '{entry.Action.Name}' не объявлено ни одним модулем");
            }

            log.LogWarning("audit_action_not_declared {Action}", entry.Action.Name);
        }

        db.Add(Map(entry));
    }

    private AuditLogEntry Map(AuditEntry entry) =>
        AuditRow.From(entry, context.Current, clock.GetUtcNow(), redactor);
}
