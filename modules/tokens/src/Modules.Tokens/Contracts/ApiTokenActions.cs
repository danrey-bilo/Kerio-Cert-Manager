using Modules.Core.Contracts.Audit;

namespace Modules.Tokens.Contracts;

/// <summary>События, которые пишет модуль токенов.</summary>
public static class ApiTokenActions
{
    /// <summary>Токен закрыт: вышли сами, отозвали, кончился срок.</summary>
    public static readonly AuditAction Revoked = new("auth.session_revoked", "Сессия закрыта");

    /// <summary>Предъявлен уже потраченный токен обновления.</summary>
    public static readonly AuditAction Reuse = new("auth.token_reuse", "Повторное использование токена");

    /// <summary>Уборка мёртвых токенов и секретов.</summary>
    public static readonly AuditAction Cleaned = new("auth.cleanup", "Уборка сессий");

    /// <summary>Заведён ключ интеграции.</summary>
    public static readonly AuditAction Created = new("apitoken.created", "Ключ интеграции заведён");

    /// <summary>Ключ интеграции отозван владельцем.</summary>
    public static readonly AuditAction KeyRevoked = new("apitoken.revoked", "Ключ интеграции отозван");

    /// <summary>Предъявлен ключ с истёкшим сроком.</summary>
    public static readonly AuditAction ExpiredPresented =
        new("apitoken.expired_presented", "Предъявлен истёкший ключ");
}
