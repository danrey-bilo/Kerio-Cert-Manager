using Modules.Core.Contracts.Audit;

namespace Modules.Settings.Contracts;

/// <summary>События журнала, которые пишет модуль настроек.</summary>
public static class SettingsActions
{
    /// <summary>Изменена системная настройка.</summary>
    public static readonly AuditAction SystemUpdate = new("settings.system.update", "Изменена настройка");
}
