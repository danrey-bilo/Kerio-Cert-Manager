using Modules.Core.Contracts.Authz;

namespace Modules.Identity.Permissions;

/// <summary>Права вокруг учётных записей — модуль объявляет их сам.</summary>
internal sealed class IdentityPermissions : IPermissionProvider
{
    /// <summary>Видеть карточку человека: флаги, блокировку, второй фактор, роли.</summary>
    public const string Read = "user.read";

    /// <summary>Видеть справочник сотрудников: имя и — по параметру проекта — адрес и отдел.</summary>
    public const string Directory = "user.directory";

    /// <summary>Завести учётную запись.</summary>
    public const string Create = "user.create";

    /// <summary>Изменить учётную запись: профиль и флаги.</summary>
    public const string Update = "user.update";

    /// <summary>Отключить учётную запись.</summary>
    public const string Disable = "user.disable";

    /// <summary>Удалить учётную запись.</summary>
    public const string Delete = "user.delete";

    /// <summary>Снять человеку второй фактор.</summary>
    public const string ResetTwoFactor = "user.reset_two_factor";

    public IReadOnlyList<PermissionDescriptor> Permissions { get; } =
    [
        // ⚠ Карточка человека — уровня администратора (решение 0110).
        new(Read, "Видеть пользователей", "Карточка человека: флаги, блокировка, второй фактор, роли.", "users",
            Tier: PermissionTier.Administer),
        new(Directory, "Видеть справочник сотрудников", "Список людей и команд для выбора: имя, по настройке — адрес и отдел.", "users",
            Tier: PermissionTier.Read),
        new(Create, "Создать пользователя",
            "Завести учётную запись, выслать приглашение заново, выдать ссылку сброса пароля — пока почты нет, ссылка приходит в ответе.",
            "users",
            IsDangerous: true,
            Tier: PermissionTier.Administer),
        new(
            Update,
            "Изменить пользователя",
            "Править профиль и флаги учётной записи.",
            "users",
            IsDangerous: true),
        new(
            Disable,
            "Отключить пользователя",
            "Закрыть человеку доступ, немедленно оборвав его сессии.",
            "users",
            IsDangerous: true),
        new(
            Delete,
            "Удалить пользователя",
            "Удалить учётную запись. Записи журнала о нём остаются.",
            "users",
            IsDangerous: true),
        new(
            ResetTwoFactor,
            "Снять второй фактор",
            "Снять человеку второй фактор, когда он потерял приложение-аутентификатор.",
            "users",
            IsDangerous: true),
    ];
}
