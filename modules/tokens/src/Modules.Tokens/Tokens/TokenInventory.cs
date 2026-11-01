using Modules.Tokens.Contracts;
using Modules.Tokens.Domain;
using Modules.Tokens.Permissions;
using Modules.Core.Contracts.Accounts;
using Modules.Core.Contracts.Audit;
using Modules.Core.Contracts.Authz;
using Modules.Core.Contracts.Tokens;
using Modules.Core.Data;
using Modules.Core.Errors;
using Microsoft.EntityFrameworkCore;

namespace Modules.Tokens.Tokens;

/// <summary>Строка инвентаря вместе с именем владельца.</summary>
internal sealed record InventoryRow(
    Guid Id,
    Guid UserId,
    string? UserName,
    string Kind,
    string? Name,
    IReadOnlyList<string> Scopes,
    DateTimeOffset CreatedAt,
    DateTimeOffset LastSeenAt,
    DateTimeOffset ExpiresAt,
    DateTimeOffset? RevokedAt);

/// <summary>Чужие предъявляемые секреты: кто чем ходит и как это закрыть.</summary>
internal sealed class TokenInventory(
    AppDbContext db,
    ITokenRevoker revoker,
    IPermissionChecker permissions,
    IEnumerable<IResourceNameResolver> names,
    ICurrentUser actor,
    TimeProvider clock,
    IAccounts accounts)
{
    /// <summary>Чем предъявляются люди.</summary>
    public async Task<IReadOnlyList<InventoryRow>> ListAsync(
        Guid? userId, string? kind, bool includeRevoked, CancellationToken cancellationToken = default)
    {
        var виды = Виды(kind);

        if (виды.Any(вид => вид != TokenKinds.ApiKey))
        {
            await ТребоватьAsync(ApiTokenPermissions.Manage, cancellationToken);
        }

        var now = clock.GetUtcNow();

        var запрос = db.Set<AuthToken>().Where(row => виды.Contains(row.Kind));

        if (userId is { } кого)
        {
            запрос = запрос.Where(row => row.UserId == кого);
        }

        if (!includeRevoked)
        {
            запрос = запрос.Where(row => row.RevokedAt == null && row.ExpiresAt > now);
        }

        var строки = await запрос
            .OrderByDescending(row => row.LastSeenAt)
            .AsNoTracking()
            .ToListAsync(cancellationToken);

        var имена = await ИменаAsync(строки, cancellationToken);

        return
        [
            .. строки.Select(row => new InventoryRow(
                row.Id,
                row.UserId,
                имена.GetValueOrDefault(row.UserId),
                row.Kind,
                row.Name,
                row.Scopes is null
                    ? []
                    : System.Text.Json.JsonSerializer.Deserialize<List<string>>(row.Scopes) ?? [],
                row.CreatedAt,
                row.LastSeenAt,
                row.ExpiresAt,
                row.RevokedAt)),
        ];
    }

    /// <summary>Закрыть одну чужую строку.</summary>
    public async Task RevokeAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var строка = await db.Set<AuthToken>()
            .Where(row => row.Id == id)
            .Select(row => new { row.Kind, row.UserId })
            .AsNoTracking()
            .FirstOrDefaultAsync(cancellationToken)
            ?? throw AppException.NotFound("Строка не найдена");

        // ⚠ ⚠ Машину отсюда не закрывают (ревизия 2026-10-26, решение 0118).
        if (строка.Kind == TokenKinds.Device)
        {
            throw new AppException(
                TokenErrorCodes.DeviceRevokeViaDevices,
                "Машину отзывает учёт машин: там гаснут и доверие, и офлайн-копия.",
                System.Net.HttpStatusCode.Conflict);
        }

        var право = строка.Kind == TokenKinds.ApiKey
            ? ApiTokenPermissions.Revoke
            : ApiTokenPermissions.RevokeAny;

        await ТребоватьAsync(право, cancellationToken);

        // ⚠ Учётной записью суперпользователя распоряжается только суперпользователь.
        if (!actor.IsSuperuser && await accounts.FlagsAsync(строка.UserId, cancellationToken) is { IsSuperuser: true })
        {
            throw new AppException(
                ErrorCodes.SuperuserFlagForbidden,
                "Входами суперпользователя распоряжается только суперпользователь.",
                System.Net.HttpStatusCode.Forbidden);
        }

        await db.InTransactionAsync(
            async token =>
            {
                // ⚠ Строку журнала пишет сам отзыв — с тем, кто отзывает, и с видом строки (решение 0110): прежде здесь стояла вторая строка о том же событии.
                await revoker.RevokeAsync(id, RevokeReasons.Revoked, token);

                await db.SaveChangesAsync(token);

                return true;
            },
            cancellationToken);
    }

    /// <summary>Какие виды спрашивают.</summary>
    private static List<string> Виды(string? kind) =>
        kind is { Length: > 0 } один ? [один] : [.. TokenKinds.All];

    private async Task ТребоватьAsync(string permission, CancellationToken cancellationToken)
    {
        if (actor.Id is not { } кто
            || !await permissions.CanAsync(кто, permission, cancellationToken: cancellationToken))
        {
            throw AppException.PermissionDenied();
        }
    }

    /// <summary>Имена владельцев — пачкой и чужим резолвером.</summary>
    private async Task<Dictionary<Guid, string?>> ИменаAsync(
        IReadOnlyCollection<AuthToken> строки, CancellationToken cancellationToken)
    {
        var резолвер = names.FirstOrDefault(candidate => candidate.ResourceType == "user");

        if (резолвер is null || строки.Count == 0)
        {
            return [];
        }

        var кого = строки.Select(row => row.UserId).Distinct().ToList();

        // ⚠ Читатель передаётся резолверу: имена он называет С ПРОВЕРКОЙ прав читателя, и не положено видеть — вернёт пусто.
        var найденные = await резолвер.ResolveAsync(кого, actor.Id ?? Guid.Empty, cancellationToken);

        return найденные.ToDictionary(пара => пара.Key, пара => (string?)пара.Value);
    }
}
