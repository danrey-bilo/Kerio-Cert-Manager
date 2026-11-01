using Modules.Core.Contracts.Authz;
using Modules.Tokens.Contracts;
using Modules.Tokens.Domain;
using Modules.Tokens.Naming;
using Modules.Core.Contracts.Audit;
using Modules.Core.Contracts.Tokens;
using Modules.Core.Data;
using Microsoft.EntityFrameworkCore;

namespace Modules.Tokens.Tokens;

/// <summary>Закрытие токенов — одно место на весь модуль.</summary>
internal sealed class TokenRevoker(AppDbContext db, IAuditWriter audit, ICurrentUser actor, TimeProvider clock) : ITokenRevoker
{
    /// <summary>Закрыть одну строку.</summary>
    public async Task<bool> RevokeAsync(
        Guid tokenId, string reason, CancellationToken cancellationToken = default)
    {
        RequireTransaction();

        var now = clock.GetUtcNow();

        var owner = await db.Set<AuthToken>()
            .Where(row => row.Id == tokenId && row.RevokedAt == null)
            .Select(row => new { row.UserId, row.Kind })
            .FirstOrDefaultAsync(cancellationToken);

        if (owner is null)
        {
            return false;
        }

        var userId = owner.UserId;

        var affected = await db.Set<AuthToken>()
            .Where(row => row.Id == tokenId && row.RevokedAt == null)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(row => row.RevokedAt, now)
                    .SetProperty(row => row.RevokedReason, reason),
                cancellationToken);

        if (affected == 0)
        {
            return false;
        }

        Record(tokenId, userId, owner.Kind, reason);

        await ExpireSecretsAsync([tokenId], now, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);

        return true;
    }

    public Task<int> RevokeAllAsync(
        Guid userId, string reason, TokenKindMask kinds, CancellationToken cancellationToken = default) =>
        ManyAsync(userId, reason, kinds, keep: null, cancellationToken);

    /// <summary>Закрыть всё, кроме одной строки.</summary>
    public Task<int> RevokeOthersAsync(
        Guid userId,
        Guid? keep,
        string reason,
        TokenKindMask kinds,
        CancellationToken cancellationToken = default) =>
        ManyAsync(userId, reason, kinds, keep, cancellationToken);

    /// <summary>Закрыть всё, что привязано к одному устройству.</summary>
    public async Task<int> RevokeByDeviceAsync(
        string deviceRef, string reason, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(deviceRef);
        RequireTransaction();

        var now = clock.GetUtcNow();

        var doomed = await db.Set<AuthToken>()
            .Where(row => row.DeviceRef == deviceRef && row.RevokedAt == null && row.ExpiresAt > now)
            .Select(row => new { row.Id, row.UserId, row.Kind })
            .ToListAsync(cancellationToken);

        if (doomed.Count == 0)
        {
            return 0;
        }

        var закрыто = await ЗакрытьAsync([.. doomed.Select(row => row.Id)], reason, now, cancellationToken);

        if (закрыто.Count == 0)
        {
            return 0;
        }

        foreach (var row in doomed.Where(row => закрыто.Contains(row.Id)))
        {
            Record(row.Id, row.UserId, row.Kind, reason);
        }

        await ExpireSecretsAsync([.. закрыто], now, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);

        return закрыто.Count;
    }

    /// <summary>Удалить строки человека насовсем.</summary>
    public async Task<int> DeleteAllAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        RequireTransaction();

        return await db.Set<AuthToken>()
            .Where(row => row.UserId == userId)
            .ExecuteDeleteAsync(cancellationToken);
    }

    private async Task<int> ManyAsync(
        Guid userId, string reason, TokenKindMask kinds, Guid? keep, CancellationToken cancellationToken)
    {
        RequireTransaction();

        var now = clock.GetUtcNow();
        var виды = Виды(kinds);

        if (виды.Count == 0)
        {
            return 0;
        }

        // ⚠ Только живые и не истёкшие.
        var строки = await db.Set<AuthToken>()
            .Where(row => row.UserId == userId
                && виды.Contains(row.Kind)
                && row.RevokedAt == null
                && row.ExpiresAt > now
                && (keep == null || row.Id != keep))
            .Select(row => new { row.Id, row.Kind })
            .ToListAsync(cancellationToken);

        if (строки.Count == 0)
        {
            return 0;
        }

        var закрыто = await ЗакрытьAsync([.. строки.Select(row => row.Id)], reason, now, cancellationToken);

        if (закрыто.Count == 0)
        {
            return 0;
        }

        foreach (var строка in строки.Where(row => закрыто.Contains(row.Id)))
        {
            Record(строка.Id, userId, строка.Kind, reason);
        }

        await ExpireSecretsAsync([.. закрыто], now, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);

        return закрыто.Count;
    }

    /// <summary>Закрыть ещё живые из списка и вернуть, какие закрыли именно мы.</summary>
    private async Task<HashSet<Guid>> ЗакрытьAsync(
        Guid[] ids, string reason, DateTimeOffset now, CancellationToken cancellationToken) =>
        [
            .. await db.Database.SqlQuery<Guid>($"""
                UPDATE auth_tokens SET revoked_at = {now}, revoked_reason = {reason}
                WHERE id = ANY({ids}) AND revoked_at IS NULL
                RETURNING id AS "Value"
                """).ToListAsync(cancellationToken),
        ];

    /// <summary>Маска в список значений колонки.</summary>
    private static List<string> Виды(TokenKindMask kinds)
    {
        var список = new List<string>(4);

        if (kinds.HasFlag(TokenKindMask.Sessions))
        {
            список.Add(TokenKinds.Session);
        }

        if (kinds.HasFlag(TokenKindMask.Devices))
        {
            список.Add(TokenKinds.Device);
        }

        if (kinds.HasFlag(TokenKindMask.ApiKeys))
        {
            список.Add(TokenKinds.ApiKey);
        }

        if (kinds.HasFlag(TokenKindMask.DownloadTickets))
        {
            список.Add(TokenKinds.DownloadTicket);
        }

        return список;
    }

    /// <summary>Строка журнала об отзыве.</summary>
    private void Record(Guid tokenId, Guid ownerId, string kind, string reason)
    {
        var кража = reason == RevokeReasons.Stolen;
        var ключ = kind == TokenKinds.ApiKey;

        var кто = кража ? ownerId : actor.Id ?? ownerId;

        audit.Write(new AuditEntry(
            кража ? ApiTokenActions.Reuse : ключ ? ApiTokenActions.KeyRevoked : ApiTokenActions.Revoked,
            ActorId: кто,
            Result: кража ? AuditResult.Deny : AuditResult.Allow,
            ResourceType: ключ ? TokenResources.ApiKey : TokenResources.Session,
            ResourceId: tokenId,
            Reason: reason,
            Changes: кто == ownerId ? null : new Dictionary<string, object?> { ["owner_id"] = ownerId }));
    }

    /// <summary>Погасить секреты закрытых строк: срок «сейчас».</summary>
    private Task ExpireSecretsAsync(
        IReadOnlyCollection<Guid> tokenIds, DateTimeOffset now, CancellationToken cancellationToken) =>
        db.Set<AuthTokenSecret>()
            .Where(row => tokenIds.Contains(row.TokenId) && row.ExpiresAt > now)
            .ExecuteUpdateAsync(setters => setters.SetProperty(row => row.ExpiresAt, now), cancellationToken);

    private void RequireTransaction()
    {
        if (db.Database.CurrentTransaction is not null)
        {
            return;
        }

        throw new InvalidOperationException(
            "Отзыв токена обязан идти внутри транзакции: секреты гасятся оператором немедленно, "
            + "а строка журнала уезжает на сохранении. Без общей транзакции доступ пропал бы, "
            + "а следа не осталось (правило 15).");
    }
}
