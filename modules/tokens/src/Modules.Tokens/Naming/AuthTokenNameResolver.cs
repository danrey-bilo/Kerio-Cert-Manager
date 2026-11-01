using System.Collections.ObjectModel;
using Modules.Tokens.Domain;
using Modules.Core.Contracts.Audit;
using Modules.Core.Contracts.Authz;
using Modules.Core.Data;
using Microsoft.EntityFrameworkCore;

namespace Modules.Tokens.Naming;

/// <summary>Называет выданные токены для журнала: откуда и чем входили.</summary>
internal sealed class AuthTokenNameResolver(AppDbContext db, IPermissionChecker permissions) : IResourceNameResolver
{
    /// <summary>Право читать учётные записи.</summary>
    private const string ForeignUserRead = "user.read";

    public string ResourceType => TokenResources.Session;

    public async Task<IReadOnlyDictionary<Guid, string>> ResolveAsync(
        IReadOnlyCollection<Guid> ids,
        Guid readerId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(ids);

        if (ids.Count == 0)
        {
            return ReadOnlyDictionary<Guid, string>.Empty;
        }

        var rows = await db.Set<AuthToken>()
            .Where(token => ids.Contains(token.Id))
            .Select(token => new Row(token.Id, token.UserId, token.Address, token.Name))
            .AsNoTracking()
            .ToListAsync(cancellationToken);

        var mine = rows.Where(row => row.UserId == readerId).ToList();
        var others = rows.Count - mine.Count;

        // ⚠ Отдельного запроса про суперпользователя ЗДЕСЬ НЕТ, и это отличие от того, что переезжало.
        var visible = others > 0
            && await permissions.CanAsync(readerId, ForeignUserRead, cancellationToken: cancellationToken)
                ? rows
                : mine;

        return visible.ToDictionary(row => row.Id, Describe);
    }

    /// <summary>Откуда и чем.</summary>
    private static string Describe(Row row) => (row.Address?.ToString(), row.Name) switch
    {
        (null, null) => "сессия",
        ({ } address, null) => address,
        (null, { } name) => name,
        ({ } address, { } name) => $"{address} · {name}",
    };

    /// <summary>Ровно те поля, что нужны для подписи.</summary>
    private sealed record Row(Guid Id, Guid UserId, System.Net.IPAddress? Address, string? Name);
}
