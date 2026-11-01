namespace Modules.Identity.Contracts;

/// <summary>Машинные коды ошибок модуля учётных записей.</summary>
public static class IdentityErrorCodes
{
    /// <summary>Неверный адрес или пароль.</summary>
    public const string InvalidCredentials = "INVALID_CREDENTIALS";

    /// <summary>Такой адрес уже занят.</summary>
    public const string UserAlreadyExists = "USER_ALREADY_EXISTS";

    /// <summary>Учётная запись уже активирована — приглашение ей больше не нужно.</summary>
    public const string UserAlreadyActivated = "USER_ALREADY_ACTIVATED";

    /// <summary>Ссылка активации или сброса недействительна: истекла, погашена, не та.</summary>
    public const string ActivationTokenInvalid = "ACTIVATION_TOKEN_INVALID";

    /// <summary>Последнего активного суперпользователя нельзя отключить или удалить.</summary>
    public const string LastSuperadmin = "LAST_SUPERADMIN";

    /// <summary>Нужен второй фактор: пароль принят, вход не завершён.</summary>
    public const string TwoFactorRequired = "TWO_FACTOR_REQUIRED";

    /// <summary>Код второго фактора не подошёл.</summary>
    public const string TwoFactorInvalid = "TWO_FACTOR_INVALID";

    /// <summary>Второй фактор уже включён.</summary>
    public const string TwoFactorAlreadyEnabled = "TWO_FACTOR_ALREADY_ENABLED";

    /// <summary>Второй фактор не заведён или не подтверждён.</summary>
    public const string TwoFactorNotEnrolled = "TWO_FACTOR_NOT_ENROLLED";

    /// <summary>Настроенный второй фактор временно недоступен; пароль не открывает повторную настройку.</summary>
    public const string TwoFactorUnavailable = "TWO_FACTOR_UNAVAILABLE";
}
