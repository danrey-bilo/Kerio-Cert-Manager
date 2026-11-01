namespace Modules.Core.Contracts.Authz;

/// <summary>Право, без которого этот эндпоинт не отвечает.</summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = true)]
public sealed class RequirePermissionAttribute(string permission) : Attribute
{
    /// <summary>Какое право требуется.</summary>
    public string Permission { get; } = permission;

    /// <summary>Тип ресурса, если право проверяется не глобально.</summary>
    public string? ResourceType { get; init; }

    /// <summary>Имя параметра маршрута или строки запроса, откуда берётся идентификатор ресурса.</summary>
    public string? From { get; init; }

    /// <summary>Ресурс необязателен: без него право проверяется глобально.</summary>
    public bool ResourceOptional { get; init; }
}

/// <summary>Эндпоинт требует входа, но не требует права.</summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method)]
public sealed class AuthenticatedEndpointAttribute(string reason) : Attribute
{
    /// <summary>Почему без права.</summary>
    public string Reason { get; } = reason;
}

/// <summary>Эндпоинт открыт даже тому, кому система велела сменить пароль.</summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method)]
public sealed class AllowWhilePasswordChangeRequiredAttribute(string reason) : Attribute
{
    /// <summary>Объяснение для того, кто читает список исключений.</summary>
    public string Reason { get; } = reason;
}

/// <summary>Эндпоинт открыт даже тому, кому система велела включить второй фактор.</summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method)]
public sealed class AllowWhileTwoFactorEnrollmentRequiredAttribute(string reason) : Attribute
{
    /// <summary>Объяснение для того, кто читает список исключений.</summary>
    public string Reason { get; } = reason;
}

/// <summary>Эндпоинт открыт машине — программе, пришедшей по ключу API.</summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method)]
public sealed class AllowApiTokenAttribute(string reason) : Attribute
{
    /// <summary>Объяснение для того, кто читает список открытого машинам.</summary>
    public string Reason { get; } = reason;
}

/// <summary>Эндпоинт принимает билет на скачивание.</summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method)]
public sealed class AllowDownloadTicketAttribute(string reason) : Attribute
{
    /// <summary>Почему билет годится здесь.</summary>
    public string Reason { get; } = reason;
}

/// <summary>Эндпоинт открыт намеренно.</summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method)]
public sealed class PublicEndpointAttribute(string reason) : Attribute
{
    /// <summary>Почему этот эндпоинт открыт.</summary>
    public string Reason { get; } = reason;
}

/// <summary>Гейта нет, потому что решение принимается по КАЖДОМУ ресурсу внутри.</summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method)]
public sealed class ResourceScopedAuthorizationAttribute(string reason) : Attribute
{
    /// <summary>Как именно сужается ответ.</summary>
    public string Reason { get; } = reason;
}

/// <summary>Маршрут проверяет права САМ, потому что конвейер до него не дотягивается.</summary>
public sealed class SelfGuardedEndpointAttribute(string reason) : Attribute
{
    /// <summary>Что проверяет сам обработчик.</summary>
    public string Reason { get; } = reason;
}
