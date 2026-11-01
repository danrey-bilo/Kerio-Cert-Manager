namespace Modules.Core.Contracts.Accounts;

/// <summary>Доступ учётной записи — роли, команды, выдачи — глазами модуля учётных записей.</summary>
public interface IAccountAccess
{
    /// <summary>Роли этих людей — если вошедшему их видно; <c>null</c> — не видно.</summary>
    Task<IReadOnlyDictionary<Guid, IReadOnlyList<AccountRole>>?> RolesAsync(
        IReadOnlyCollection<Guid> accountIds, CancellationToken cancellationToken = default);

    /// <summary>Выдать роли только что заведённому человеку — в транзакции заведения.</summary>
    Task AssignOnCreateAsync(
        Guid accountId, IReadOnlyCollection<Guid> roleIds, CancellationToken cancellationToken = default);

    /// <summary>Унести действующий доступ удалённого человека — в транзакции удаления.</summary>
    Task<IReadOnlyDictionary<string, int>> RemoveAsync(
        Guid accountId, CancellationToken cancellationToken = default);
}

/// <summary>Роль в списке людей: имя и идентификатор.</summary>
public sealed record AccountRole(Guid Id, string Name);
