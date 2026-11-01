using Modules.Core.Contracts.Audit;

namespace Modules.Audit.Contracts;

/// <summary>События, которые пишет сам модуль журнала.</summary>
public static class AuditActions
{
    /// <summary>Кто-то выгрузил журнал.</summary>
    public static readonly AuditAction Export = new("audit.export", "Выгрузка журнала");

    /// <summary>Срок хранения удалил старые партиции.</summary>
    public static readonly AuditAction Retention = new("audit.retention", "Удаление старых записей");

    /// <summary>Срок хранения назначил удаление партиции — после отсрочки (решение 0110).</summary>
    public static readonly AuditAction RetentionScheduled = new("audit.retention_scheduled", "Назначено удаление старых записей");

    /// <summary>Поднятый срок хранения отменил назначенное удаление партиции.</summary>
    public static readonly AuditAction RetentionCancelled = new("audit.retention_cancelled", "Удаление старых записей отменено");

    /// <summary>Суточный якорь цепочки: голова каждого месяца на этот момент.</summary>
    public static readonly AuditAction Anchor = new("audit.anchor", "Якорь цепочки журнала");

    /// <summary>Проверка права: разрешено или отказано.</summary>
    public static readonly AuditAction PermissionCheck = new("permission_check", "Проверка права");

    /// <summary>Массовое раскрытие секретов одним человеком за короткое время.</summary>
    public static readonly AuditAction MassReveal = new("security.mass_reveal", "Массовое раскрытие секретов");
}
