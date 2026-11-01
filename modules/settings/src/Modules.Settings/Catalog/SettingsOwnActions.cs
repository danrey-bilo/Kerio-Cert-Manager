using Modules.Settings.Contracts;
using Modules.Core.Contracts.Audit;

namespace Modules.Settings.Catalog;

/// <summary>События настроек — модуль объявляет их так же, как любой другой.</summary>
internal sealed class SettingsOwnActions : IAuditActionProvider
{
    public IReadOnlyList<AuditActionGroup> Groups { get; } =
    [
        new("settings", [SettingsActions.SystemUpdate], "Настройки"),
    ];
}
