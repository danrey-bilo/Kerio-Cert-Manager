using Modules.Identity.Contracts;
using Modules.Core.Contracts.Audit;

namespace Modules.Identity.Catalog;

/// <summary>События модуля для общего справочника.</summary>
internal sealed class IdentityOwnActions : IAuditActionProvider
{
    public IReadOnlyList<AuditActionGroup> Groups { get; } =
    [
        new(
            "users",
            [
                IdentityActions.UserCreated,
                IdentityActions.UserUpdated,
                IdentityActions.UserDisabled,
                IdentityActions.UserEnabled,
                IdentityActions.UserUnlocked,
                IdentityActions.UserDeleted,
                IdentityActions.InvitationSent,
                IdentityActions.UserActivated,
                IdentityActions.ActivationRejected,
                IdentityActions.PasswordChanged,
                IdentityActions.PasswordResetIssued,
                // ⚠ Оба имени кодов восстановления остаются в списке своего.
                IdentityActions.RecoveryCodeUsed,
                IdentityActions.RecoveryCodesReissued,
                IdentityActions.TwoFactorEnabled,
                IdentityActions.TwoFactorDisabled,
                IdentityActions.TwoFactorMethodChanged,
                IdentityActions.TwoFactorReset,
                IdentityActions.StepUp,
                IdentityActions.SuperuserBootstrapped,
            ],
            "Пользователи"),
        new(
            "auth",
            [
                IdentityActions.LoggedIn,
                IdentityActions.LinksCleaned,
                IdentityActions.TrustedClientGranted,
                IdentityActions.TrustedClientRenamed,
                IdentityActions.TrustedClientRevoked,
            ],
            "Вход"),
    ];
}
