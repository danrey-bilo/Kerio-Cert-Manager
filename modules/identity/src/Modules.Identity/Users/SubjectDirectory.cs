using Modules.Identity.Domain;
using Modules.Core.Contracts.Authz;
using Modules.Core.Data;
using Microsoft.EntityFrameworkCore;

namespace Modules.Identity.Users;

/// <summary>Кто жив — для соседних модулей.</summary>
internal sealed class SubjectDirectory(AppDbContext db) : ISubjectDirectory
{
    public async Task<IReadOnlySet<Guid>> LiveAsync(
        IReadOnlyCollection<Guid> ids, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(ids);

        if (ids.Count == 0)
        {
            return new HashSet<Guid>();
        }

        var живые = await db.Set<User>()
            .AsNoTracking()
            .Where(человек => ids.Contains(человек.Id))
            .Where(человек => человек.DeletedAt == null && человек.IsActive)
            .Select(человек => человек.Id)
            .ToListAsync(cancellationToken);

        return живые.ToHashSet();
    }
}
