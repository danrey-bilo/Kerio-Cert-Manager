using Modules.Core.Contracts.Authz;
using Modules.Core.Contracts.Tokens;

namespace Modules.Tokens.Authz;

/// <summary>Ключ интеграции не может больше своего хозяина — и больше своей области.</summary>
internal sealed class ScopedPermissionChecker(
    IPermissionChecker inner,
    ICurrentUser actor,
    PresentedToken presented,
    DangerousPermissions dangerous) : IPermissionChecker
{
    /// <summary>Кто держит право глобально — <b>без сужения</b>, как есть.</summary>
    public Task<IReadOnlyCollection<Guid>> WhoCanAsync(
        string permission, CancellationToken cancellationToken = default) =>
        inner.WhoCanAsync(permission, cancellationToken);

    public async Task<bool> CanAsync(
        Guid userId,
        string permission,
        string? resourceType = null,
        Guid? resourceId = null,
        CancellationToken cancellationToken = default)
    {
        if (Область(userId) is not { } scope)
        {
            return await inner.CanAsync(userId, permission, resourceType, resourceId, cancellationToken);
        }

        // ⚠ Сначала сужение, потом внутренний.
        return Открыто(scope, permission)
            && await inner.CanAsync(userId, permission, resourceType, resourceId, cancellationToken);
    }

    /// <summary>Карта прав, сужённая тем же правилом.</summary>
    public async Task<IReadOnlyList<string>> EffectiveAsync(
        Guid userId,
        string? resourceType = null,
        Guid? resourceId = null,
        CancellationToken cancellationToken = default)
    {
        var карта = await inner.EffectiveAsync(userId, resourceType, resourceId, cancellationToken);

        return Область(userId) is { } scope ? Сузить(scope, карта) : карта;
    }

    public async Task<IReadOnlyDictionary<Guid, IReadOnlyList<string>>> EffectiveManyAsync(
        Guid userId,
        string resourceType,
        IReadOnlyCollection<Guid> resourceIds,
        CancellationToken cancellationToken = default)
    {
        var карты = await inner.EffectiveManyAsync(userId, resourceType, resourceIds, cancellationToken);

        if (Область(userId) is not { } scope)
        {
            return карты;
        }

        return карты.ToDictionary(пара => пара.Key, пара => Сузить(scope, пара.Value));
    }

    /// <summary>Область, которой сужается ответ.</summary>
    private HashSet<string>? Область(Guid userId)
    {
        if (actor.Id != userId)
        {
            return null;
        }

        if (presented.Kind != TokenKinds.ApiKey)
        {
            return null;
        }

        // ⚠ Ключ без области — пустая область, а не «вся».
        return [.. presented.Identity?.Scopes ?? []];
    }

    private bool Открыто(HashSet<string> scope, string permission) =>
        scope.Contains(permission) && !dangerous.Contains(permission);

    private IReadOnlyList<string> Сузить(HashSet<string> scope, IReadOnlyList<string> карта) =>
        [.. карта.Where(permission => Открыто(scope, permission))];
}
