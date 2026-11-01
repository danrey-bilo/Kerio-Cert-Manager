namespace Modules.Core.Contracts.Authz;

/// <summary>Какое правило решило вопрос.</summary>
public enum AccessRule
{
    /// <summary>Наибольшие права: проходит всё, и объяснять больше нечего.</summary>
    Superuser,

    /// <summary>Отключён или удалён — не проходит ничего, даже выданного раньше.</summary>
    SubjectInactive,

    /// <summary>Ресурса нет.</summary>
    ResourceMissing,

    /// <summary>Явный запрет на узле цепочки.</summary>
    Denied,

    /// <summary>Разрешение, выданное на самом ресурсе или на его предке.</summary>
    GrantedOnResource,

    /// <summary>Глобальное право из роли — последняя надежда, а не первая.</summary>
    GlobalRole,

    /// <summary>Ничего не подошло.</summary>
    DefaultDeny,
}

/// <summary>Одна выдача, участвовавшая в разборе.</summary>
public sealed record AccessTrace(
    GrantSubject SubjectType,
    Guid SubjectId,
    string ResourceType,
    Guid ResourceId,
    GrantEffect Effect,
    DateTimeOffset? ExpiresAt,
    bool Expired);

/// <summary>Почему у человека есть или нет этого права на этом ресурсе.</summary>
public sealed record AccessExplanation(
    bool Allowed,
    AccessRule Rule,
    IReadOnlyList<ResourceNode> Chain,
    IReadOnlyList<AccessTrace> Grants,
    ResourceNode? DecidedOn);

/// <summary>Объяснить решение о доступе.</summary>
public interface IAccessExplainer
{
    /// <summary>Почему у этого человека есть или нет этого права на этом ресурсе.</summary>
    Task<AccessExplanation> ExplainAsync(
        Guid userId,
        string permission,
        string resourceType,
        Guid resourceId,
        CancellationToken cancellationToken = default);
}
