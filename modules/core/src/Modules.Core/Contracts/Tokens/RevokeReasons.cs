namespace Modules.Core.Contracts.Tokens;

/// <summary>Почему токен кончился.</summary>
public static class RevokeReasons
{
    /// <summary>Человек вышел сам.</summary>
    public const string SignedOut = "signed_out";

    /// <summary>Отозвал он же из списка сессий или администратор.</summary>
    public const string Revoked = "revoked";

    /// <summary>Предъявлен уже потраченный токен обновления.</summary>
    public const string Stolen = "token_reuse";

    /// <summary>Учётная запись отключена.</summary>
    public const string UserInactive = "user_inactive";

    /// <summary>Сменили пароль — значит выгоняем всех, кто мог его знать.</summary>
    public const string PasswordChanged = "password_changed";

    /// <summary>Человек включил второй фактор — прежним ключам не место.</summary>
    public const string TwoFactorEnabled = "two_factor_enabled";

    /// <summary>Администратор снял человеку второй фактор — телефон потерян или украден.</summary>
    public const string TwoFactorReset = "two_factor_reset";

    /// <summary>Устройство отозвано — человеком или администратором.</summary>
    public const string DeviceRevoked = "device_revoked";

    /// <summary>Устройству выдан новый токен — прежние гаснут.</summary>
    public const string DeviceReissued = "device_reissued";
}
