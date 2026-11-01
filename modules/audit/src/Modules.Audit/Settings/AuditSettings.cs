using Modules.Core.Contracts.Settings;

namespace Modules.Audit.Settings;

/// <summary>Настройки журнала — модуль объявляет их сам.</summary>
internal static class AuditSettings
{
    /// <summary>Через сколько месяцев удалять старые записи.</summary>
    public static readonly SettingKey<int?> RetentionMonths = new("audit.retention_months",
        Default: null,
        "Audit retention, months",
        "After how many months old entries are deleted. Empty — keep forever.",
        "audit",
        IsSecurity: true,
        Minimum: 1, Maximum: 120);

    /// <summary>Потолок строк одной выгрузки.</summary>
    public static readonly SettingKey<int> ExportRowLimit = new("audit.export_row_limit",
        Default: 10_000,
        "Export row limit",
        "How many rows a single export returns. Truncation is marked in the export itself.",
        "audit",
        IsSecurity: true,
        Minimum: 10, Maximum: 1_000_000);

    /// <summary>Писать ли разрешённые проверки прав.</summary>
    public static readonly SettingKey<bool> LogPermissionAllows = new("audit.log_permission_allows",
        Default: false,
        "Log allowed permission checks",
        "Denials are always logged. Allows — only with this setting, while investigating.",
        "audit",
        IsSecurity: true);

    /// <summary>Сколько раскрытий за окно считать массовыми.</summary>
    public static readonly SettingKey<int> MassRevealThreshold = new("audit.mass_reveal_threshold",
        Default: 30,
        "Mass reveal threshold",
        "How many secret reveals within the window count as suspicious.",
        "audit",
        IsSecurity: true,
        Minimum: 2, Maximum: 10_000);

    /// <summary>Длина окна для порога массового раскрытия, секунд.</summary>
    public static readonly SettingKey<int> MassRevealWindowSeconds = new("audit.mass_reveal_window_seconds",
        Default: 3600,
        "Mass reveal window, seconds",
        "Over what period secret reveals are counted.",
        "audit",
        IsSecurity: true,
        Minimum: 60, Maximum: 86_400);
}

/// <summary>Объявление настроек журнала для общего экрана настроек.</summary>
internal sealed class AuditSettingDeclarations : ISettingDeclarationProvider
{
    public IReadOnlyList<ISettingDeclaration> Declarations { get; } =
    [
        AuditSettings.RetentionMonths,
        AuditSettings.ExportRowLimit,
        AuditSettings.LogPermissionAllows,
        AuditSettings.MassRevealThreshold,
        AuditSettings.MassRevealWindowSeconds,
    ];
}
