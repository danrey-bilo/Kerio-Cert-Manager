using System.Security.Cryptography;
using System.Text;
using System.Buffers.Text;
using Modules.Identity.Contracts;
using Modules.Identity.Domain;
using Modules.Identity.Naming;
using Modules.Core.Contracts.Audit;
using Modules.Core.Contracts.Tokens;
using Modules.Core.Data;
using Modules.Core.Errors;
using Microsoft.EntityFrameworkCore;

namespace Modules.Identity.Users;

internal sealed record TrustedGrant(Guid Id, string Token, DateTimeOffset ExpiresAt)
{
    /// <summary>Печать без секретов: запись попадает в лог, в отладчик и в текст исключения.</summary>
    private bool PrintMembers(System.Text.StringBuilder builder)
    {
        builder.Append("секреты скрыты: Token");

        return true;
    }
}

/// <summary>Управляемые записи доверия; секрет никогда не входит в список устройств (0085).</summary>
internal sealed class TrustedClients(AppDbContext db, ITokenRevoker revoker, IAuditWriter audit, TimeProvider clock, IdentityModuleOptions? options = null) : IClientTrust
{
    internal static string Ref(Guid id) => "trust:" + id.ToString("D");
    internal static Guid? Id(string? reference) => reference is not null && reference.StartsWith("trust:", StringComparison.Ordinal)
        && Guid.TryParse(reference[6..], out var id) ? id : null;
    private static byte[] Hash(string token) => SHA256.HashData(Encoding.UTF8.GetBytes(token));

    internal Task<List<IdentityToken>> ListAsync(Guid userId, CancellationToken cancellationToken) => db.Set<IdentityToken>()
        .AsNoTracking().Where(row => row.UserId == userId && row.Purpose == Purposes.TrustedDevice)
        .OrderBy(row => row.UsedAt != null || row.ExpiresAt <= clock.GetUtcNow())
        .ThenByDescending(row => row.CreatedAt).ThenByDescending(row => row.Id).Take(200).ToListAsync(cancellationToken);

    internal Task<IdentityToken?> FindAsync(string token, Guid userId, bool lockRow, CancellationToken cancellationToken)
    {
        var hash = Hash(token);
        var query = lockRow
            ? db.Set<IdentityToken>().FromSqlInterpolated($"SELECT * FROM identity_tokens WHERE token_hash = {hash} AND user_id = {userId} AND purpose = 'trusted_device' FOR UPDATE")
            : db.Set<IdentityToken>().AsNoTracking().Where(row => row.TokenHash == hash && row.UserId == userId && row.Purpose == Purposes.TrustedDevice);
        return query.FirstOrDefaultAsync(cancellationToken);
    }

    internal async Task<TrustedGrant> IssueAsync(Guid userId, string kind, string? name, RequestOrigin origin,
        int days, string? existingToken, CancellationToken cancellationToken, Guid? keepSession = null)
    {
        if (kind is not ("browser" or "desktop" or "extension")) throw new AppException(ErrorCodes.Validation, "Недопустимый вид клиента");
        var now = clock.GetUtcNow();
        var existing = existingToken is { Length: > 0 } ? await FindAsync(existingToken, userId, true, cancellationToken) : null;
        if (existing is { UsedAt: null, ClientKind: null })
        {
            existing.UsedAt = now;
            await revoker.RevokeOthersAsync(userId, keepSession, RevokeReasons.Revoked, TokenKindMask.Sessions, cancellationToken);
        }
        var row = existing is { UsedAt: null } && existing.ClientKind == kind ? existing : new IdentityToken
        {
            Id = Guid.CreateVersion7(),
            UserId = userId,
            Purpose = Purposes.TrustedDevice,
        };
        if (row != existing) db.Add(row);
        var secret = Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32));
        row.TokenHash = Hash(secret);
        row.CreatedAt = now;
        row.ExpiresAt = now.AddDays(days);
        row.LastSeenAt = now;
        row.ClientKind = kind;
        row.ClientName = string.IsNullOrWhiteSpace(name) ? kind == "desktop" ? options?.DesktopClientName ?? "Desktop" : "Browser" : name.Trim();
        if (row.ClientName.Length > 300) throw new AppException(ErrorCodes.Validation, "Имя клиента слишком длинное");
        row.UserAgent = origin.UserAgent is { } agent ? agent[..Math.Min(agent.Length, 512)] : null;
        row.Address = origin.Address;
        audit.Write(new AuditEntry(IdentityActions.TrustedClientGranted, ActorId: userId,
            ResourceType: IdentityResources.User, ResourceId: userId,
            Changes: new Dictionary<string, object?> { ["client_id"] = row.Id, ["client_kind"] = kind }));
        return new TrustedGrant(row.Id, secret, row.ExpiresAt);
    }

    internal Task RenameAsync(Guid userId, Guid id, string name, CancellationToken cancellationToken) => db.InTransactionAsync(async token =>
    {
        var row = await db.Set<IdentityToken>().FirstOrDefaultAsync(row => row.Id == id && row.UserId == userId && row.Purpose == Purposes.TrustedDevice, token)
            ?? throw AppException.NotFound("Устройство не найдено");
        var trimmed = name.Trim();
        if (trimmed.Length is < 1 or > 300) throw new AppException(ErrorCodes.Validation, "Имя устройства: от 1 до 300 символов");
        row.ClientName = trimmed;
        audit.Write(new AuditEntry(IdentityActions.TrustedClientRenamed, ActorId: userId, ResourceType: IdentityResources.User, ResourceId: userId,
            Changes: new Dictionary<string, object?> { ["client_id"] = id }));
        await db.SaveChangesAsync(token);
    }, cancellationToken);

    internal Task RevokeAsync(Guid userId, Guid id, CancellationToken cancellationToken) => db.InTransactionAsync(async token =>
    {
        var row = await db.Set<IdentityToken>().FromSqlInterpolated($"SELECT * FROM identity_tokens WHERE id = {id} AND user_id = {userId} AND purpose = 'trusted_device' FOR UPDATE")
            .FirstOrDefaultAsync(token) ?? throw AppException.NotFound("Устройство не найдено");
        if (row.UsedAt is not null) return;
        row.UsedAt = clock.GetUtcNow();
        await revoker.RevokeByDeviceAsync(Ref(id), RevokeReasons.Revoked, token);
        if (row.ClientKind is null) await revoker.RevokeAllAsync(userId, RevokeReasons.Revoked, TokenKindMask.Sessions, token);
        if (row.DeviceId is { } deviceId) await revoker.RevokeByDeviceAsync(deviceId.ToString("D"), RevokeReasons.Revoked, token);
        audit.Write(new AuditEntry(IdentityActions.TrustedClientRevoked, ActorId: userId, ResourceType: IdentityResources.User, ResourceId: userId,
            Changes: new Dictionary<string, object?> { ["client_id"] = id }));
        await db.SaveChangesAsync(token);
    }, cancellationToken);

    public async Task BindDeviceAsync(Guid userId, string? clientRef, Guid deviceId, CancellationToken cancellationToken = default)
    {
        if (Id(clientRef) is not { } id) return;
        if (db.Database.CurrentTransaction is null) throw new InvalidOperationException("Привязка требует транзакцию");
        var row = await db.Set<IdentityToken>().FromSqlInterpolated($"SELECT * FROM identity_tokens WHERE id = {id} AND user_id = {userId} AND purpose = 'trusted_device' FOR UPDATE")
            .FirstOrDefaultAsync(cancellationToken);
        if (row is null || row.UsedAt is not null || row.ExpiresAt <= clock.GetUtcNow())
            throw new AppException(ErrorCodes.SessionExpired, "Доверие завершено. Войдите заново.", System.Net.HttpStatusCode.Unauthorized);
        if (row.ClientKind is not ("desktop" or "extension")) return;
        row.DeviceId = deviceId;
    }

    public async Task RevokeDeviceAsync(Guid userId, Guid deviceId, CancellationToken cancellationToken = default)
    {
        if (db.Database.CurrentTransaction is null) throw new InvalidOperationException("Отзыв требует транзакцию");
        var rows = await db.Set<IdentityToken>().FromSqlInterpolated($"SELECT * FROM identity_tokens WHERE user_id = {userId} AND device_id = {deviceId} AND purpose = 'trusted_device' AND used_at IS NULL ORDER BY id FOR UPDATE")
            .ToListAsync(cancellationToken);
        foreach (var row in rows)
        {
            row.UsedAt = clock.GetUtcNow();
            await revoker.RevokeByDeviceAsync(Ref(row.Id), RevokeReasons.DeviceRevoked, cancellationToken);
            audit.Write(new AuditEntry(IdentityActions.TrustedClientRevoked, ResourceType: IdentityResources.User, ResourceId: userId,
                Changes: new Dictionary<string, object?> { ["client_id"] = row.Id, ["device_id"] = deviceId }));
        }
    }
}
