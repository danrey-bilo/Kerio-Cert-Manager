using Modules.Core.Contracts.Authz;

namespace Modules.Tokens.Permissions;

/// <summary>Права модуля: всё про <b>чужие</b> предъявляемые секреты.</summary>
internal sealed class ApiTokenPermissions : IPermissionProvider
{
    /// <summary>Видеть чужие ключи интеграций.</summary>
    public const string Read = "apitoken.read";

    /// <summary>Отозвать чужой ключ интеграции.</summary>
    public const string Revoke = "apitoken.revoke";

    /// <summary>Видеть, чем вообще предъявляется человек: чужие сессии и устройства.</summary>
    public const string Manage = "apitoken.manage";

    /// <summary>Отозвать одну чужую сессию или устройство.</summary>
    public const string RevokeAny = "token.revoke";

    public IReadOnlyList<PermissionDescriptor> Permissions { get; } =
    [
        new(
            Read,
            "Видеть чужие ключи интеграций",
            "Видеть ключи интеграций других людей: имя, область, срок, когда пользовались.",
            "apitokens",

            // ⚠ Уровень — администратор (решение 0110, находка 8).
            Tier: PermissionTier.Administer),

        new(
            Revoke,
            "Отзывать чужие ключи интеграций",
            "Закрывать ключи интеграций других людей.",
            "apitokens",
            IsDangerous: true,
            Tier: PermissionTier.Write),

        new(
            Manage,
            "Видеть чужие сессии и устройства",
            "Видеть, чем человек предъявляется системе: браузеры, настольные клиенты, билеты.",
            "apitokens",
            IsDangerous: true,
            Tier: PermissionTier.Administer),

        new(
            RevokeAny,
            "Отзывать чужие сессии",
            "Закрывать одну сессию или устройство другого человека.",
            "apitokens",
            IsDangerous: true,
            Tier: PermissionTier.Administer),
    ];
}
