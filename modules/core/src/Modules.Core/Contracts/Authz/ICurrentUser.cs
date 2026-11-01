using Modules.Core.Contracts.Audit;

namespace Modules.Core.Contracts.Authz;

/// <summary>Кто пришёл с этим запросом.</summary>
public interface ICurrentUser
{
    /// <summary>Идентификатор или <c>null</c>, если запрос неаутентифицирован.</summary>
    Guid? Id { get; }

    /// <summary>Проходит любую проверку прав.</summary>
    bool IsSuperuser { get; }

    /// <summary>Чем пришли: человек, ключ API.</summary>
    AuditActorType ActorType { get; }

    /// <summary>Какой сессией пришли, если способ входа их различает.</summary>
    Guid? SessionId => null;
    /// <summary>Несекретная метка клиента, связанного с этой сессией (0085).</summary>
    string? ClientRef => null;

    /// <summary>Чем именно предъявились: вид секрета из <c>TokenKinds</c>.</summary>
    string? Kind => null;

    /// <summary>Системе нужно, чтобы этот человек сменил пароль, — и до тех пор он ограничен.</summary>
    bool MustChangePassword => false;

    /// <summary>Системе нужно, чтобы этот человек включил второй фактор, — и до тех пор он ограничен.</summary>
    bool MustEnrollTwoFactor => false;

    /// <summary>Когда личность подтверждали последний раз — на <b>этом</b> предъявленном токене.</summary>
    DateTimeOffset? StepUpAt => null;
}

/// <summary>Спросить, можно ли этому человеку это действие над этим объектом.</summary>
public interface IPermissionChecker
{
    /// <summary>Есть ли у человека это право — глобально или на этот ресурс.</summary>
    Task<bool> CanAsync(
        Guid userId,
        string permission,
        string? resourceType = null,
        Guid? resourceId = null,
        CancellationToken cancellationToken = default);

    /// <summary>Всё, что человек может — глобально или на этом ресурсе.</summary>
    Task<IReadOnlyList<string>> EffectiveAsync(
        Guid userId,
        string? resourceType = null,
        Guid? resourceId = null,
        CancellationToken cancellationToken = default);

    /// <summary>То же на списке ресурсов одного типа: <c>id → что можно</c>.</summary>
    Task<IReadOnlyDictionary<Guid, IReadOnlyList<string>>> EffectiveManyAsync(
        Guid userId,
        string resourceType,
        IReadOnlyCollection<Guid> resourceIds,
        CancellationToken cancellationToken = default);

    /// <summary>Обратный вопрос: <b>кто</b> держит это право глобально.</summary>
    Task<IReadOnlyCollection<Guid>> WhoCanAsync(
        string permission, CancellationToken cancellationToken = default);
}

/// <summary>Насколько тяжёлое право.</summary>
public enum PermissionTier
{
    /// <summary>Только смотреть.</summary>
    Read,

    /// <summary>Обычная работа: завести, изменить, удалить своё.</summary>
    Write,

    /// <summary>Распоряжаться системой или чужим доступом.</summary>
    Administer,
}

/// <summary>Объявление пермишена модулем.</summary>
public sealed record PermissionDescriptor(
    string Key,
    string Title,
    string Description,
    string Category,
    bool IsDangerous = false,
    PermissionTier Tier = PermissionTier.Administer,
    bool Deprecated = false,
    string? SupersededBy = null,
    bool IsResourceScoped = false);

/// <summary>Модуль объявляет свои права.</summary>
public interface IPermissionProvider
{
    /// <summary>Права этого модуля.</summary>
    IReadOnlyList<PermissionDescriptor> Permissions { get; }
}
