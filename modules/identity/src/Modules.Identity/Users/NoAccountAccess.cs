using System.Net;
using Modules.Core.Contracts.Accounts;
using Modules.Core.Errors;

namespace Modules.Identity.Users;

/// <summary>Доступ учётной записи там, где модуля доступа нет: ролей нет, уносить нечего.</summary>
internal sealed class NoAccountAccess : IAccountAccess
{
    public Task<IReadOnlyDictionary<Guid, IReadOnlyList<AccountRole>>?> RolesAsync(
        IReadOnlyCollection<Guid> accountIds, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyDictionary<Guid, IReadOnlyList<AccountRole>>?>(null);

    public Task AssignOnCreateAsync(
        Guid accountId, IReadOnlyCollection<Guid> roleIds, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(roleIds);

        return roleIds.Count == 0
            ? Task.CompletedTask
            : throw new AppException(
                ErrorCodes.Validation, "Роли в этой установке не подключены", HttpStatusCode.BadRequest);
    }

    public Task<IReadOnlyDictionary<string, int>> RemoveAsync(
        Guid accountId, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyDictionary<string, int>>(new Dictionary<string, int>());
}
