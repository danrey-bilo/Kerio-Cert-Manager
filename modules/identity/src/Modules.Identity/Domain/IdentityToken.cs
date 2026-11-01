namespace Modules.Identity.Domain;

/// <summary>Одноразовая ссылка: приглашение или сброс пароля.</summary>
internal sealed class IdentityToken
{
    public Guid Id { get; set; }

    /// <summary>Кому выдана.</summary>
    public Guid UserId { get; set; }

    /// <summary>SHA-256 от самой ссылки.</summary>
    public byte[] TokenHash { get; set; } = [];

    /// <summary>Зачем выдана: <c>activation</c> или <c>reset</c>.</summary>
    public string Purpose { get; set; } = Purposes.Activation;

    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>До какого момента действует.</summary>
    public DateTimeOffset ExpiresAt { get; set; }

    /// <summary>Когда ею воспользовались.</summary>
    public DateTimeOffset? UsedAt { get; set; }

    /// <summary>Привязка короткого кода к вызову входа или сессии; открытого секрета здесь нет.</summary>
    public string? Binding { get; set; }

    /// <summary>Подписи записи доверенного клиента; у остальных назначений пусто.</summary>
    public string? ClientName { get; set; }
    public string? ClientKind { get; set; }
    public string? UserAgent { get; set; }
    public System.Net.IPAddress? Address { get; set; }
    public DateTimeOffset? LastSeenAt { get; set; }
    public Guid? DeviceId { get; set; }
}

/// <summary>Назначения ссылок.</summary>
internal static class Purposes
{
    /// <summary>Активация по приглашению.</summary>
    public const string Activation = "activation";

    /// <summary>Сброс забытого пароля.</summary>
    public const string Reset = "reset";

    /// <summary>Все допустимые значения — для ограничения в базе.</summary>
    public const string TwoFactor = "two_factor";

    /// <summary>Одноразовый код второго фактора, ушедший письмом.</summary>
    public const string LoginCode = "login_code";
    public const string StepUpCode = "step_up_code";

    /// <summary>Доверие машине: пока эта строка жива, вход с неё не спрашивает второй фактор.</summary>
    public const string TrustedDevice = "trusted_device";

    /// <summary>Окно первичной настройки суперпользователя: пока строка жива, вход без фактора даёт сессию настройки.</summary>
    public const string SuperuserSetup = "superuser_setup";

    public static readonly string[] All = [Activation, Reset, TwoFactor, LoginCode, StepUpCode, TrustedDevice, SuperuserSetup];
}
