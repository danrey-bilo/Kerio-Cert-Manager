using Modules.Core.Contracts.Authz;

namespace Modules.Settings.Permissions;

/// <summary>Права настроек — модуль объявляет их сам.</summary>
internal sealed class SettingsPermissions : IPermissionProvider
{
    /// <summary>Видеть экран настроек: справочник, значения, источник.</summary>
    public const string Read = "settings.read";

    /// <summary>Менять ключи без признака безопасности.</summary>
    public const string Manage = "settings.manage";

    /// <summary>Менять ключи с признаком безопасности.</summary>
    public const string SecurityManage = "settings.security.manage";

    public IReadOnlyList<PermissionDescriptor> Permissions { get; } =
    [
        new(
            Read,
            "Видеть настройки",
            "Видеть значения настроек установки, их умолчания и источник.",
            "settings",

            // ⚠ ⚠ НЕ ОПАСНОЕ, и это перенос из Python, а не решение.
            Tier: PermissionTier.Read),
        new(
            Manage,
            "Менять настройки",
            "Менять настройки установки, кроме тех, что помечены как настройки защиты.",
            "settings",

            // ⚠ Не опасное осознанно: под этим правом лежат формат даты, плотность списков, сроки хранения корзины.
            Tier: PermissionTier.Administer),
        new(
            SecurityManage,
            "Менять настройки защиты",
            "Менять политику паролей, сроки сессий, блокировку входа и требование второго фактора.",
            "settings",

            // ⚠ ⚠ ОПАСНОЕ, и это единственная пометка, которая здесь не обсуждается.
            IsDangerous: true,
            Tier: PermissionTier.Administer),
    ];
}
