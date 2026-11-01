using Modules.Core.Contracts.Audit;

namespace Modules.Core.Contracts.Tokens;

/// <summary>Что известно о предъявленном секрете, если он жив.</summary>
public sealed record TokenIdentity(
    Guid UserId,
    Guid TokenId,
    AuditActorType ActorType,
    IReadOnlyList<string>? Scopes = null,
    DateTimeOffset? StepUpAt = null,
    string? Kind = null,
    string? ClientRef = null);

/// <summary>Проверить предъявленный секрет: жив ли он и чей.</summary>
public interface ITokenIntrospector
{
    /// <summary>Наш ли это формат вообще.</summary>
    bool Recognises(string presented);

    /// <summary>Проверить секрет и сказать, чей он.</summary>
    Task<TokenIdentity?> IntrospectAsync(string presented, CancellationToken cancellationToken = default);
}
