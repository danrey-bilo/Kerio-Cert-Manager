using System.Net;

namespace Modules.Audit.Domain;

/// <summary>Строка журнала.</summary>
internal sealed class AuditLogEntry
{
    /// <summary>Суррогатный ключ.</summary>
    public long Id { get; set; }

    /// <summary>Когда записано.</summary>
    public DateTimeOffset Ts { get; set; }

    /// <summary>Кто.</summary>
    public Guid? ActorId { get; set; }

    /// <summary>Человек, токен или система.</summary>
    public string ActorType { get; set; } = "user";

    /// <summary>Адрес.</summary>
    public IPAddress? ActorIp { get; set; }

    /// <summary>Чем пришли.</summary>
    public string? UserAgent { get; set; }

    /// <summary>Какое право проверялось, если событие про проверку прав.</summary>
    public string? Permission { get; set; }

    /// <summary>Имя события из справочника.</summary>
    public string Action { get; set; } = string.Empty;

    /// <summary>Тип ресурса: <c>vault</c>, <c>credential</c>.</summary>
    public string? ResourceType { get; set; }

    /// <summary>Идентификатор ресурса.</summary>
    public Guid? ResourceId { get; set; }

    /// <summary>Разрешено или отказано.</summary>
    public string Result { get; set; } = "allow";

    /// <summary>Почему отказано.</summary>
    public string? Reason { get; set; }

    /// <summary>Что изменилось — готовый JSON.</summary>
    public string? ChangesJson { get; set; }

    /// <summary>Номер обращения — сшивает строку с логами.</summary>
    public string? RequestId { get; set; }

    /// <summary>Какая сессия.</summary>
    public Guid? SessionId { get; set; }

    /// <summary>Когда действие произошло по часам клиента.</summary>
    public DateTimeOffset? OccurredAt { get; set; }
}
