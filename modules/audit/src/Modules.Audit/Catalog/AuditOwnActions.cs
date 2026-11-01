using Modules.Audit.Contracts;
using Modules.Core.Contracts.Audit;

namespace Modules.Audit.Catalog;

/// <summary>События самого журнала — он объявляет их так же, как любой другой модуль.</summary>
internal sealed class AuditOwnActions : IAuditActionProvider
{
    public IReadOnlyList<AuditActionGroup> Groups { get; } =
    [
        new("audit", [AuditActions.Export, AuditActions.Retention, AuditActions.RetentionScheduled, AuditActions.RetentionCancelled, AuditActions.Anchor], "Журнал"),
        new("security", [AuditActions.PermissionCheck, AuditActions.MassReveal], "Защита"),
    ];
}
