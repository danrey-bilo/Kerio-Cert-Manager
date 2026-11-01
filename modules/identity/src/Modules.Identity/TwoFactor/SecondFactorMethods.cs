namespace Modules.Identity.TwoFactor;

/// <summary>Чем подтверждают вход.</summary>
internal static class SecondFactorMethods
{
    /// <summary>Одноразовый код письмом.</summary>
    public const string Email = "email";

    /// <summary>Код из приложения-аутентификатора.</summary>
    public const string Totp = "totp";

    /// <summary>Фактора нет.</summary>
    public const string None = "none";

    /// <summary>Все три, в порядке убывания надёжности.</summary>
    public static readonly IReadOnlyList<string> All = [Totp, Email, None];

    /// <summary>Что человек может выбрать себе.</summary>
    public static readonly IReadOnlyList<string> Choosable = [Totp, Email];
}
