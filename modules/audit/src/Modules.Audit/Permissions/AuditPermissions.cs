using Modules.Core.Contracts.Authz;

namespace Modules.Audit.Permissions;

/// <summary>Права журнала — модуль объявляет их сам.</summary>
internal sealed class AuditPermissions : IPermissionProvider
{
    /// <summary>Видеть журнал.</summary>
    public const string Read = "audit.read";

    /// <summary>Выгрузить журнал файлом.</summary>
    public const string Export = "audit.export";

    public IReadOnlyList<PermissionDescriptor> Permissions { get; } =
    [
        new(
            Read,
            "Видеть аудит",
            "Видеть журнал действий всех пользователей.",
            "audit",
            IsDangerous: true,

            // ⚠ Уровень «чтение», хотя право и опасное: одно про то, что действие делает (ничего не меняет), другое — про то, что оно показывает.
            Tier: PermissionTier.Read),
        new(
            Export,
            "Выгрузить аудит",
            "Выгрузить журнал аудита файлом.",
            "audit",
            IsDangerous: true),
    ];
}
