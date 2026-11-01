using Modules.Tokens.Contracts;
using Modules.Tokens.Domain;
using Modules.Tokens.Settings;
using Modules.Core.Contracts.Authz;
using Modules.Core.Contracts.Settings;
using Modules.Core.Contracts.Tokens;
using Modules.Core.Data;
using Modules.Core.Errors;
using Microsoft.EntityFrameworkCore;

namespace Modules.Tokens.Tokens;

/// <summary>Ключ интеграции глазами его владельца.</summary>
internal sealed record ApiKeyView(
    Guid Id,
    string? Name,
    IReadOnlyList<string> Scopes,
    DateTimeOffset CreatedAt,
    DateTimeOffset LastSeenAt,
    DateTimeOffset ExpiresAt,
    DateTimeOffset? RevokedAt);

/// <summary>Свои ключи интеграции: завести, перечислить, отозвать.</summary>
internal sealed class ApiKeyService(
    AppDbContext db,
    ITokenIssuer issuer,
    ITokenRevoker revoker,
    IPermissionChecker permissions,
    IEnumerable<IPermissionProvider> declared,
    ICurrentUser actor,
    ISettingsReader settings,
    TimeProvider clock)
{
    /// <summary>Свои ключи — и живые, и мёртвые.</summary>
    public async Task<IReadOnlyList<ApiKeyView>> ListAsync(CancellationToken cancellationToken = default)
    {
        var rows = await db.Set<AuthToken>()
            .Where(row => row.UserId == Хозяин && row.Kind == TokenKinds.ApiKey)
            .OrderByDescending(row => row.CreatedAt)
            .AsNoTracking()
            .ToListAsync(cancellationToken);

        return [.. rows.Select(Показать)];
    }

    /// <summary>Завести ключ.</summary>
    public async Task<(ApiKeyView Key, string Secret)> CreateAsync(
        string? name,
        IReadOnlyList<string> scopes,
        TimeSpan? lifetime,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scopes);

        await ПроверитьОбластьAsync(scopes, cancellationToken);

        try
        {
            return await db.InTransactionAsync(
                async token =>
                {
                    // ⚠ ⚠ Счёт и вставка — под замком на человека (ревизия 2026-10-26, решение 0118).
                    var замок = "apitokens:" + Хозяин.ToString("N");

                    await db.Database.ExecuteSqlAsync($"SELECT pg_advisory_xact_lock(hashtextextended({замок}, 0))", token);
                    await ПроверитьПределAsync(token);

                    var pair = await issuer.IssueAsync(
                        new TokenIssueRequest(
                            Хозяин, TokenKinds.ApiKey, name, Scopes: scopes, Lifetime: lifetime),
                        token);

                    await db.SaveChangesAsync(token);

                    var row = await db.Set<AuthToken>()
                        .Where(candidate => candidate.Id == pair.TokenId)
                        .AsNoTracking()
                        .FirstAsync(token);

                    return (Показать(row), pair.Access);
                },
                cancellationToken);
        }
        catch (DbUpdateException ошибка) when (ошибка.InnerException?.Message.Contains("ux_auth_tokens_user_name", StringComparison.Ordinal) == true)
        {
            // ⚠ Имя занято живым или истёкшим, но не отозванным ключом (решение 0118): прежде — 500.
            throw new AppException(
                TokenErrorCodes.ApiKeyNameTaken,
                "Ключ с таким именем уже есть — отзовите прежний (в том числе истёкший) или назовите иначе.",
                System.Net.HttpStatusCode.Conflict,
                new Dictionary<string, object?> { ["name"] = name });
        }
    }

    /// <summary>Отозвать свой ключ.</summary>
    public async Task RevokeAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var свой = await db.Set<AuthToken>()
            .Where(row => row.Id == id && row.UserId == Хозяин && row.Kind == TokenKinds.ApiKey)
            .AsNoTracking()
            .AnyAsync(cancellationToken);

        if (!свой)
        {
            throw AppException.NotFound("Ключ не найден");
        }

        // ⚠ ⚠ Строку журнала пишет сам отзыв (`TokenRevoker`): своё событие ключа, актор — тот, кто отзывает.
        await db.InTransactionAsync(
            async token =>
            {
                await revoker.RevokeAsync(id, RevokeReasons.SignedOut, token);
                await db.SaveChangesAsync(token);

                return true;
            },
            cancellationToken);
    }

    /// <summary>Область: только объявленное, не опасное и не шире своих прав.</summary>
    private async Task ПроверитьОбластьAsync(IReadOnlyList<string> scopes, CancellationToken cancellationToken)
    {
        if (scopes.Count == 0)
        {
            throw new AppException(
                ErrorCodes.Validation,
                "Ключ без единого права ничего не может — назовите хотя бы одно.",
                System.Net.HttpStatusCode.BadRequest);
        }

        var объявленные = declared
            .SelectMany(provider => provider.Permissions)
            .ToDictionary(permission => permission.Key, StringComparer.Ordinal);

        var неизвестные = scopes.Where(scope => !объявленные.ContainsKey(scope)).ToList();

        if (неизвестные.Count > 0)
        {
            // ⚠ Отказ, а не тихое отбрасывание.
            throw new AppException(
                ErrorCodes.PermissionUnknown,
                $"Неизвестные права: {string.Join(", ", неизвестные)}",
                System.Net.HttpStatusCode.BadRequest);
        }

        var опасные = scopes.Where(scope => объявленные[scope].IsDangerous).ToList();

        if (опасные.Count > 0)
        {
            throw new AppException(
                TokenErrorCodes.TokenScopeForbidden,
                $"Эти права нельзя дать ключу интеграции: {string.Join(", ", опасные)}",
                System.Net.HttpStatusCode.BadRequest,
                new Dictionary<string, object?> { ["forbidden"] = опасные });
        }

        // ⚠ Спрашивается по одному, а не картой: карта отвечает про глобальные права, а право может быть выдано на ресурсе.
        var чужие = new List<string>();

        foreach (var scope in scopes)
        {
            if (!await permissions.CanAsync(Хозяин, scope, cancellationToken: cancellationToken))
            {
                чужие.Add(scope);
            }
        }

        if (чужие.Count > 0)
        {
            throw new AppException(
                TokenErrorCodes.TokenScopeForbidden,
                $"У вас самих нет этих прав: {string.Join(", ", чужие)}",
                System.Net.HttpStatusCode.BadRequest,
                new Dictionary<string, object?> { ["forbidden"] = чужие });
        }
    }

    /// <summary>Предел ключей на человека.</summary>
    private async Task ПроверитьПределAsync(CancellationToken cancellationToken)
    {
        var предел = Math.Max(1, await settings.GetAsync(TokenSettings.MaxPerUser, cancellationToken));
        var now = clock.GetUtcNow();

        var живых = await db.Set<AuthToken>()
            .Where(row => row.UserId == Хозяин
                && row.Kind == TokenKinds.ApiKey
                && row.RevokedAt == null
                && row.ExpiresAt > now)
            .CountAsync(cancellationToken);

        if (живых >= предел)
        {
            throw new AppException(
                ErrorCodes.QuotaExceeded,
                $"Ключей интеграции можно завести не больше {предел}.",
                System.Net.HttpStatusCode.Conflict,
                new Dictionary<string, object?>
                {
                    ["quota"] = "apitokens.max_per_user",
                    ["limit"] = предел,
                    ["current"] = живых,
                });
        }
    }

    private Guid Хозяин =>
        actor.Id ?? throw new AppException(
            ErrorCodes.Unauthenticated, "Требуется вход", System.Net.HttpStatusCode.Unauthorized);

    private static ApiKeyView Показать(AuthToken row) =>
        new(
            row.Id,
            row.Name,
            row.Scopes is null
                ? []
                : System.Text.Json.JsonSerializer.Deserialize<List<string>>(row.Scopes) ?? [],
            row.CreatedAt,
            row.LastSeenAt,
            row.ExpiresAt,
            row.RevokedAt);
}
