using Modules.Core.Contracts.Audit;

namespace Modules.Identity.Contracts;

/// <summary>События, которые пишет модуль личностей.</summary>
public static class IdentityActions
{
    public static readonly AuditAction TrustedClientGranted = new("user.client_trusted", "Клиенту выдано доверие");
    public static readonly AuditAction TrustedClientRevoked = new("user.client_revoked", "Доверие клиенту отозвано");
    public static readonly AuditAction TrustedClientRenamed = new("user.client_renamed", "Клиент переименован");
    /// <summary>Заведена учётная запись.</summary>
    public static readonly AuditAction UserCreated = new("user.create", "Заведён пользователь");

    /// <summary>Изменён профиль или флаги.</summary>
    public static readonly AuditAction UserUpdated = new("user.update", "Изменён пользователь");

    /// <summary>Учётная запись отключена.</summary>
    public static readonly AuditAction UserDisabled = new("user.disable", "Отключён пользователь");

    /// <summary>Учётная запись включена обратно.</summary>
    public static readonly AuditAction UserEnabled = new("user.enable", "Включён пользователь");

    /// <summary>Администратор снял человеку второй фактор.</summary>
    public static readonly AuditAction TwoFactorReset = new("user.totp_reset", "Снят второй фактор");

    /// <summary>Перевыпущены коды восстановления.</summary>
    public static readonly AuditAction RecoveryCodesReissued =
        new("user.recovery_codes_reissued", "Перевыпущены коды восстановления");

    /// <summary>Снята блокировка входа после неудачных попыток.</summary>
    public static readonly AuditAction UserUnlocked = new("user.unlock", "Снята блокировка входа");

    /// <summary>Учётная запись удалена (мягко).</summary>
    public static readonly AuditAction UserDeleted = new("user.delete", "Удалён пользователь");

    /// <summary>Выслано приглашение.</summary>
    public static readonly AuditAction InvitationSent = new("user.invitation_sent", "Выслано приглашение");

    /// <summary>Человек активировал учётную запись по ссылке.</summary>
    public static readonly AuditAction UserActivated = new("user.activate", "Активирована учётная запись");

    /// <summary>Попытка активации по недействительной ссылке.</summary>
    public static readonly AuditAction ActivationRejected = new("user.activation_rejected", "Отказ в активации");

    /// <summary>Пароль сменён.</summary>
    public static readonly AuditAction PasswordChanged = new("user.password_changed", "Сменён пароль");

    /// <summary>Администратор выдал ссылку сброса пароля.</summary>
    public static readonly AuditAction PasswordResetIssued =
        new("user.password_reset_issued", "Выдана ссылка сброса пароля");

    /// <summary>Включён второй фактор.</summary>
    public static readonly AuditAction TwoFactorEnabled = new("user.totp_enabled", "Включён второй фактор");

    /// <summary>Отключён второй фактор.</summary>
    public static readonly AuditAction TwoFactorDisabled = new("user.totp_disabled", "Отключён второй фактор");

    /// <summary>Человек выбрал себе способ второго фактора.</summary>
    public static readonly AuditAction TwoFactorMethodChanged = new(
        "user.two_factor_method", "Выбран способ второго фактора");

    /// <summary>Личность переподтверждена перед действием, которое этого требует.</summary>
    public static readonly AuditAction StepUp = new("user.step_up", "Личность переподтверждена");

    /// <summary>Вход по коду восстановления.</summary>
    public static readonly AuditAction RecoveryCodeUsed = new("user.recovery_used", "Вход по коду восстановления");

    /// <summary>Первый суперпользователь заведён командой.</summary>
    public static readonly AuditAction SuperuserBootstrapped =
        new("user.bootstrap", "Заведён первый суперпользователь");

    /// <summary>Попытка входа — удачная или нет.</summary>
    public static readonly AuditAction LoggedIn = new("auth.login", "Вход");

    /// <summary>Уборка одноразовых ссылок.</summary>
    public static readonly AuditAction LinksCleaned = new("identity.cleanup", "Уборка ссылок");
}
