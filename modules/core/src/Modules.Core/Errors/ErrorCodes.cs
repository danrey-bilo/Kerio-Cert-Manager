namespace Modules.Core.Errors;

/// <summary>Машинные коды ошибок, общие для всей системы.</summary>
public static class ErrorCodes
{
    /// <summary>Непредвиденный сбой.</summary>
    public const string Internal = "INTERNAL_ERROR";

    /// <summary>Тело или параметры запроса не прошли проверку.</summary>
    public const string Validation = "VALIDATION_ERROR";

    /// <summary>Нет удостоверения или оно недействительно.</summary>
    public const string Unauthenticated = "UNAUTHENTICATED";

    /// <summary>Удостоверение есть, прав не хватает.</summary>
    public const string PermissionDenied = "PERMISSION_DENIED";

    /// <summary>Объект не найден — или найден, но показывать его этому человеку нельзя.</summary>
    public const string NotFound = "NOT_FOUND";

    /// <summary>Зависимости недоступны: база, кэш, партиции журнала.</summary>
    public const string NotReady = "NOT_READY";

    /// <summary>Слишком часто.</summary>
    public const string RateLimited = "RATE_LIMITED";

    /// <summary>Тело запроса больше предела этого адреса (413).</summary>
    public const string PayloadTooLarge = "PAYLOAD_TOO_LARGE";

    /// <summary>Адрес есть, а этого метода у него нет (405).</summary>
    public const string MethodNotAllowed = "METHOD_NOT_ALLOWED";

    /// <summary>Курсор пагинации испорчен или не от этой выдачи.</summary>
    public const string InvalidCursor = "INVALID_CURSOR";

    /// <summary>Сборка клиента не удостоверена сервером или слишком стара.</summary>
    public const string BuildNotTrusted = "BUILD_NOT_TRUSTED";

    /// <summary>Пароль требуется сменить: до этого доступно только это действие.</summary>
    public const string PasswordChangeRequired = "PASSWORD_CHANGE_REQUIRED";

    /// <summary>Система требует включить второй фактор: до этого доступно только его включение.</summary>
    public const string TwoFactorEnrollmentRequired = "TWO_FACTOR_ENROLLMENT_REQUIRED";

    /// <summary>Этот эндпоинт машине не открыт.</summary>
    public const string ApiTokenNotAllowed = "API_TOKEN_NOT_ALLOWED";

    /// <summary>Сессия кончилась: истёк срок или её больше нет.</summary>
    public const string SessionExpired = "SESSION_EXPIRED";

    /// <summary>Сессия закрыта: вышли, отозвали, обнаружили кражу токена.</summary>
    public const string SessionRevoked = "SESSION_REVOKED";

    /// <summary>Учётная запись отключена или удалена.</summary>
    public const string UserInactive = "USER_INACTIVE";

    /// <summary>Токен предъявлен, но его срок вышел.</summary>
    public const string TokenExpired = "TOKEN_EXPIRED";

    /// <summary>Устройство отозвано: править его нечего.</summary>
    public const string DeviceRevoked = "DEVICE_REVOKED";

    /// <summary>Право с таким именем не объявлено ни одним модулем.</summary>
    public const string PermissionUnknown = "PERMISSION_UNKNOWN";

    /// <summary>Выдать пытаются больше, чем есть у себя.</summary>
    public const string PermissionEscalation = "PERMISSION_ESCALATION";

    /// <summary>Флагом суперпользователя распоряжается только суперпользователь.</summary>
    public const string SuperuserFlagForbidden = "SUPERUSER_FLAG_FORBIDDEN";

    /// <summary>Показ секрета требует свежего подтверждения личности — 403.</summary>
    public const string SecondFactorRequired = "SECOND_FACTOR_REQUIRED";

    /// <summary>Предел исчерпан: ключей, хранилищ, записей.</summary>
    public const string QuotaExceeded = "QUOTA_EXCEEDED";

    /// <summary>Пароль не удовлетворяет политике.</summary>
    public const string PasswordPolicyViolation = "PASSWORD_POLICY_VIOLATION";

    /// <summary>Почта не настроена: слать некуда.</summary>
    public const string MailNotConfigured = "MAIL_NOT_CONFIGURED";

    /// <summary>Срок доступа задан в прошлом.</summary>
    public const string InvalidExpiry = "INVALID_EXPIRY";
}
