namespace Modules.Identity.Contracts;

/// <summary>Что сделало восстановление доступа из консоли (<c>IdentityModule.ResetAccessAsync</c>, решение 0120).</summary>
public sealed record AccessResetResult(
    Guid UserId,
    string Email,
    bool IsActive,
    bool HadTwoFactor,
    int SessionsRevoked);
