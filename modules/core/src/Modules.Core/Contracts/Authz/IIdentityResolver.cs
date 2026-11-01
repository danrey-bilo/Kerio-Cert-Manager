using Modules.Core.Contracts.Audit;

namespace Modules.Core.Contracts.Authz;

/// <summary>То немногое из запроса, что нужно, чтобы понять, кто пришёл.</summary>
public interface IIdentityRequest
{
    /// <summary>Значение заголовка или <c>null</c>.</summary>
    string? Header(string name);

    /// <summary>Значение куки или <c>null</c>.</summary>
    string? Cookie(string name);
}

/// <summary>Кем оказался пришедший.</summary>
public sealed record ResolvedIdentity(
    Guid UserId,
    bool IsSuperuser,
    AuditActorType ActorType,
    Guid? SessionId = null,
    bool MustChangePassword = false,
    bool MustEnrollTwoFactor = false,
    DateTimeOffset? StepUpAt = null,
    string? Kind = null,
    string? ClientRef = null);

/// <summary>Посмотри на запрос и скажи, кто это, или промолчи.</summary>
public interface IIdentityResolver
{
    /// <summary>Распознать пришедшего.</summary>
    Task<ResolvedIdentity?> ResolveAsync(IIdentityRequest request, CancellationToken cancellationToken = default);
}
