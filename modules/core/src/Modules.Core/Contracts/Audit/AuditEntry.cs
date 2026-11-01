namespace Modules.Core.Contracts.Audit;

/// <summary>Кто совершил действие.</summary>
public enum AuditActorType
{
    /// <summary>Человек.</summary>
    User,

    /// <summary>Программа по токену API.</summary>
    ApiToken,

    /// <summary>Сама система: фоновые задачи, срок хранения, истечение сессий.</summary>
    System,
}

/// <summary>Чем кончилось.</summary>
public enum AuditResult
{
    /// <summary>Разрешено.</summary>
    Allow,

    /// <summary>Отказано.</summary>
    Deny,
}

/// <summary>Событие для записи в журнал.</summary>
public sealed record AuditEntry(
    AuditAction Action,
    Guid? ActorId = null,
    AuditActorType? ActorType = null,
    AuditResult Result = AuditResult.Allow,
    string? Permission = null,
    string? ResourceType = null,
    Guid? ResourceId = null,
    string? Reason = null,
    IReadOnlyDictionary<string, object?>? Changes = null,
    DateTimeOffset? OccurredAt = null);

/// <summary>Запись события в журнал.</summary>
public interface IAuditWriter
{
    /// <summary>Добавить событие в текущую транзакцию.</summary>
    void Write(AuditEntry entry);
}
