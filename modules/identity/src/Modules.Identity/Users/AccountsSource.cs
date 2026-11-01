using Modules.Core.Contracts.Accounts;
using Modules.Identity.Authz;
using Modules.Identity.Storage;
using Microsoft.EntityFrameworkCore;

namespace Modules.Identity.Users;

/// <summary>Учётные записи для соседних модулей — строкой без пароля и состояния входа.</summary>
internal sealed class AccountsSource(UserRepository users, RequestUserFlags shared) : IAccounts
{
    public IQueryable<AccountRow> Query() =>
        users.Live.Select(user => new AccountRow
        {
            Id = user.Id,
            Email = user.Email,
            DisplayName = user.DisplayName,
            IsActive = user.IsActive,
            IsSuperuser = user.IsSuperuser,
        });

    public async Task<AccountFlags?> FlagsAsync(Guid accountId, CancellationToken cancellationToken = default)
    {
        if (shared.TryGet(accountId, out var переданные))
        {
            return переданные;
        }

        var found = await users.Live
            .Where(user => user.Id == accountId)
            .Select(user => new AccountFlags(user.IsActive, user.IsSuperuser))
            .AsNoTracking()
            .FirstOrDefaultAsync(cancellationToken);

        shared.Remember(accountId, found);

        return found;
    }
}
