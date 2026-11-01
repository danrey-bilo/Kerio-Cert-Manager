namespace Modules.Core.Contracts.Authz;

/// <summary>Кому выдан доступ.</summary>
public enum GrantSubject
{
    /// <summary>Человеку.</summary>
    User,

    /// <summary>Команде — то есть всем, кто в ней состоит сейчас.</summary>
    Team,
}

/// <summary>Что делает выданное право.</summary>
public enum GrantEffect
{
    /// <summary>Разрешает.</summary>
    Allow,

    /// <summary>Запрещает — и запрет сильнее любого разрешения, на любом уровне цепочки.</summary>
    Deny,
}

/// <summary>Что выдать.</summary>
public sealed record AccessGrantDraft(
    GrantSubject SubjectType,
    Guid SubjectId,
    string ResourceType,
    Guid ResourceId,
    string Permission,
    GrantEffect Effect = GrantEffect.Allow,
    DateTimeOffset? ExpiresAt = null);

/// <summary>Что выдано.</summary>
public sealed record AccessGrantView(
    Guid Id,
    GrantSubject SubjectType,
    Guid SubjectId,
    string ResourceType,
    Guid ResourceId,
    string Permission,
    GrantEffect Effect,
    DateTimeOffset? ExpiresAt,
    DateTimeOffset CreatedAt,
    Guid? CreatedBy);

/// <summary>Как обстоит дело с владением ресурсом.</summary>
public enum OwnerStanding
{
    /// <summary>Есть действующая выдача живому субъекту.</summary>
    Alive,

    /// <summary>Выдача есть, и срок её вышел.</summary>
    Expired,

    /// <summary>Выдача действует, но её хозяин отключён.</summary>
    SubjectInactive,

    /// <summary>Выдача действует, а субъекта за ней нет.</summary>
    SubjectMissing,

    /// <summary>Действующих выдач на это право нет.</summary>
    None,
}

/// <summary>Кому принадлежит ресурс и в каком это состоянии.</summary>
public sealed record ResourceOwner(OwnerStanding Standing, GrantSubject? SubjectType, Guid? SubjectId);

/// <summary>Выдать и отобрать доступ к <b>своему</b> ресурсу.</summary>
public interface IAccessGrants
{
    /// <summary>Выдать доступ.</summary>
    Task<Guid> GrantAsync(AccessGrantDraft draft, CancellationToken cancellationToken = default);

    /// <summary>Выдать одному субъекту НАБОР прав на одном ресурсе — одной выборкой, одним сохранением и одной строкой журнала на набор.</summary>
    Task<IReadOnlyList<Guid>> GrantSetAsync(
        GrantSubject subjectType,
        Guid subjectId,
        string resourceType,
        Guid resourceId,
        IReadOnlyList<string> permissions,
        GrantEffect effect = GrantEffect.Allow,
        DateTimeOffset? expiresAt = null,
        CancellationToken cancellationToken = default);

    /// <summary>Отобрать одну выдачу на <b>своём</b> ресурсе.</summary>
    Task<bool> RevokeAsync(
        string resourceType, Guid grantId, CancellationToken cancellationToken = default);

    /// <summary>Отобрать всё, что выдано на этот ресурс.</summary>
    Task<int> RevokeOnResourceAsync(
        string resourceType, Guid resourceId, CancellationToken cancellationToken = default);

    /// <summary>Отобрать всё, что выдано на НЕСКОЛЬКО ресурсов одного типа, — одной выборкой и одной строкой журнала на вызов.</summary>
    Task<int> RevokeOnResourcesAsync(
        string resourceType, IReadOnlyCollection<Guid> resourceIds, CancellationToken cancellationToken = default);

    /// <summary>Что выдано на этом ресурсе — для экрана «кому открыто».</summary>
    Task<IReadOnlyList<AccessGrantView>> OnResourceAsync(
        string resourceType, Guid resourceId, CancellationToken cancellationToken = default);

    /// <summary>Что выдано этому человеку или этой команде — для экрана «к чему есть доступ».</summary>
    Task<IReadOnlyList<AccessGrantView>> OfSubjectAsync(
        GrantSubject subjectType, Guid subjectId, CancellationToken cancellationToken = default);

    /// <summary>Есть ли у этих ресурсов действующая выдача этого права <b>живому</b> субъекту.</summary>
    Task<IReadOnlyDictionary<Guid, ResourceOwner>> OwnersAsync(
        string resourceType,
        string permission,
        IReadOnlyCollection<Guid> resourceIds,
        CancellationToken cancellationToken = default);
}
