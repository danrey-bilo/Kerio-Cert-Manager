using System.Collections.ObjectModel;
using Modules.Identity.Domain;
using Modules.Identity.Permissions;
using Modules.Core.Contracts.Audit;
using Modules.Core.Contracts.Authz;
using Modules.Core.Data;
using Microsoft.EntityFrameworkCore;

namespace Modules.Identity.Naming;

/// <summary>Называет учётные записи для журнала.</summary>
internal sealed class UserNameResolver(AppDbContext db, IPermissionChecker permissions) : IResourceNameResolver
{
    public string ResourceType => IdentityResources.User;

    /// <summary>Можно ли этому читателю называть учётные записи.</summary>
    private async Task<bool> MayNameAsync(Guid readerId, CancellationToken cancellationToken)
    {
        var superuser = await db.Set<User>()
            .Where(user => user.Id == readerId && user.DeletedAt == null && user.IsActive)
            .Select(user => user.IsSuperuser)
            .FirstOrDefaultAsync(cancellationToken);

        return superuser
            || await permissions.CanAsync(readerId, IdentityPermissions.Read, cancellationToken: cancellationToken);
    }

    public async Task<IReadOnlyDictionary<Guid, string>> ResolveAsync(
        IReadOnlyCollection<Guid> ids,
        Guid readerId,
        CancellationToken cancellationToken = default)
    {
        if (ids.Count == 0)
        {
            return ReadOnlyDictionary<Guid, string>.Empty;
        }

        if (!await MayNameAsync(readerId, cancellationToken))
        {
            return ReadOnlyDictionary<Guid, string>.Empty;
        }

        var rows = await db.Set<User>()
            .Where(user => ids.Contains(user.Id))
            .Select(user => new { user.Id, user.Email, user.DisplayName })
            .AsNoTracking()
            .ToListAsync(cancellationToken);

        return rows.ToDictionary(
            row => row.Id,
            row => string.IsNullOrWhiteSpace(row.DisplayName) ? row.Email : $"{row.DisplayName} ({row.Email})");
    }
}
