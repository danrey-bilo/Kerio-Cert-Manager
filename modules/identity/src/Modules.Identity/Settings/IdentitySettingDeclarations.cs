using Modules.Core.Contracts.Settings;

namespace Modules.Identity.Settings;

/// <summary>Объявление настроек модуля для общего экрана настроек.</summary>
internal sealed class IdentitySettingDeclarations : ISettingDeclarationProvider
{
    public IReadOnlyList<ISettingDeclaration> Declarations { get; } =
    [
        IdentitySettings.PasswordMinLength,
        IdentitySettings.RequireUppercase,
        IdentitySettings.RequireLowercase,
        IdentitySettings.RequireDigit,
        IdentitySettings.RequireSymbol,
        IdentitySettings.InvitationTtlMinutes,
        IdentitySettings.SecondFactor,
        IdentitySettings.LockoutThreshold,
        IdentitySettings.LockoutMinutes,
        IdentitySettings.LockoutEscalation,
        IdentitySettings.LockoutMaxMinutes,
        IdentitySettings.LinkRetentionDays,
        IdentitySettings.TwoFactorChallengeMinutes,

        IdentitySettings.StepUpMinutes,

        // ⚠ Решение 0112: снята молча вместе с настройками TOTP.
        IdentitySettings.TrustedDeviceDays,
    ];
}
