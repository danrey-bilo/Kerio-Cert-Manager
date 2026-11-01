using Modules.Identity.Settings;
using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using Modules.Identity.Domain;
using Modules.Core.Contracts.Settings;
using Modules.Core.Data;
using Modules.Crypto;
using Microsoft.EntityFrameworkCore;

namespace Modules.Identity.Users;

/// <summary>Выданная ссылка: что показать человеку и до какого момента она живёт.</summary>
internal sealed record Invitation(string Token, DateTimeOffset ExpiresAt)
{
    /// <summary>Печать без секретов: запись попадает в лог, в отладчик и в текст исключения.</summary>
    private bool PrintMembers(System.Text.StringBuilder builder)
    {
        builder.Append("секреты скрыты: Token");

        return true;
    }
}

/// <summary>Одноразовые ссылки: выдача, чтение, гашение.</summary>
internal sealed class Invitations(AppDbContext db, ISettingsReader settings, TimeProvider clock, IIntegrityProtector integrity)
{
    /// <summary>Гашение ссылки — условным обновлением с возвратом владельца.</summary>
    private const string ConsumeSql =
        """
        UPDATE identity_tokens SET used_at = now()
        WHERE token_hash = {0} AND purpose = {1} AND used_at IS NULL AND expires_at > now()
        RETURNING user_id AS "Value"
        """;

    /// <summary>Выдать новую ссылку.</summary>
    public async Task<Invitation> IssueAsync(
        Guid userId,
        string purpose,
        TimeSpan? ttl = null,
        bool quenchPrevious = true,
        CancellationToken cancellationToken = default,
        string? binding = null)
    {
        var minutes = Math.Max(5, await settings.GetAsync(IdentitySettings.InvitationTtlMinutes, cancellationToken));
        var now = clock.GetUtcNow();
        var expires = now + (ttl ?? TimeSpan.FromMinutes(minutes));

        // ⚠ ⚠ Прежние гасятся ВСЕГДА, кроме доверия машине.
        if (quenchPrevious)
        {
            await QuenchAsync(userId, purpose, now, cancellationToken);
        }

        var token = Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32));

        db.Add(new IdentityToken
        {
            UserId = userId,
            TokenHash = Hash(token),
            Purpose = purpose,
            ExpiresAt = expires,
            CreatedAt = now,
            Binding = binding,
        });

        return new Invitation(token, expires);
    }

    /// <summary>Посмотреть, кому выдана ссылка, <b>не гася её</b>.</summary>
    public Task<int> QuenchAsync(
        Guid userId, string purpose, DateTimeOffset now, CancellationToken cancellationToken = default) =>
        db.Set<IdentityToken>()
            .Where(row => row.UserId == userId && row.Purpose == purpose && row.UsedAt == null)
            .ExecuteUpdateAsync(row => row.SetProperty(x => x.UsedAt, now), cancellationToken);

    /// <summary>Есть ли у человека живая строка этого назначения — значение не нужно.</summary>
    public Task<bool> HasLiveAsync(
        Guid userId, string purpose, DateTimeOffset now, CancellationToken cancellationToken = default) =>
        db.Set<IdentityToken>().AnyAsync(
            row => row.UserId == userId && row.Purpose == purpose && row.UsedAt == null && row.ExpiresAt > now,
            cancellationToken);

    public async Task<Guid?> PeekAsync(
        string token, string purpose, CancellationToken cancellationToken = default)
    {
        var hash = Hash(token);
        var now = clock.GetUtcNow();

        var row = await db.Set<IdentityToken>()
            .AsNoTracking()
            .FirstOrDefaultAsync(
                candidate => candidate.TokenHash == hash
                    && candidate.Purpose == purpose
                    && candidate.UsedAt == null
                    && candidate.ExpiresAt > now,
                cancellationToken);

        return row?.UserId;
    }

    /// <summary>Погасить ссылку и узнать владельца.</summary>
    public async Task<Guid?> ConsumeAsync(
        string token, string purpose, CancellationToken cancellationToken = default)
    {
        var owners = await db.Database
            .SqlQueryRaw<Guid>(ConsumeSql, Hash(token), purpose)
            .ToListAsync(cancellationToken);

        return owners.Count == 1 ? owners[0] : null;
    }

    /// <summary>Выдать человеку КОД — шесть цифр, которые он введёт с экрана.</summary>
    public async Task<Invitation> IssueCodeAsync(
        Guid userId, string purpose, TimeSpan ttl, string code, CancellationToken cancellationToken = default,
        string? binding = null)
    {
        var now = clock.GetUtcNow();
        var expires = now + ttl;

        await db.Set<IdentityToken>()
            .Where(row => row.UserId == userId && row.Purpose == purpose && row.Binding == binding)
            .ExecuteDeleteAsync(cancellationToken);

        db.Add(new IdentityToken
        {
            UserId = userId,
            TokenHash = CodeHash(userId, code, purpose, binding),
            Purpose = purpose,
            ExpiresAt = expires,
            CreatedAt = now,
            Binding = binding,
        });

        return new Invitation(code, expires);
    }

    /// <summary>Погасить КОД этого человека.</summary>
    public async Task<Guid?> ConsumeCodeAsync(
        Guid userId, string code, string purpose, CancellationToken cancellationToken = default,
        string? binding = null)
    {
        var owners = await db.Database
            .SqlQueryRaw<Guid>(ConsumeCodeSql, CodeHash(userId, code, purpose, binding), userId, purpose)
            .ToListAsync(cancellationToken);

        return owners.Count == 1 ? owners[0] : null;
    }

    private const string ConsumeCodeSql =
        """
        UPDATE identity_tokens SET used_at = now()
        WHERE token_hash = {0} AND user_id = {1} AND purpose = {2} AND used_at IS NULL AND expires_at > now()
        RETURNING user_id AS "Value"
        """;

    /// <summary>SHA-256 от кода, привязанного к человеку.</summary>
    private byte[] CodeHash(Guid userId, string code, string purpose, string? binding) =>
        SHA256.HashData(Encoding.UTF8.GetBytes(integrity.Protect(
            "identity.code.verifier", $"{userId:N}:{purpose}:{binding}", code)));

    public static string CodeBinding(string challenge) => Convert.ToHexString(Hash(challenge));

    public Task<string?> PeekBindingAsync(string token, string purpose, CancellationToken cancellationToken) =>
        db.Set<IdentityToken>().AsNoTracking().Where(row => row.TokenHash == Hash(token) && row.Purpose == purpose)
            .Select(row => row.Binding).FirstOrDefaultAsync(cancellationToken);

    /// <summary>SHA-256 от ссылки.</summary>
    private static byte[] Hash(string token) => SHA256.HashData(Encoding.UTF8.GetBytes(token));
}
