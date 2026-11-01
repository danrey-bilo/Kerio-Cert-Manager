using Modules.Core.Contracts.Tokens;
using Modules.Tokens.Contracts;
using Modules.Tokens.Domain;
using Modules.Tokens.Naming;
using Modules.Tokens.Settings;
using Modules.Core.Contracts.Audit;
using Modules.Core.Contracts.Settings;
using Modules.Core.Data;
using Microsoft.EntityFrameworkCore;

namespace Modules.Tokens.Retention;

/// <summary>Сколько чего убрано за один проход.</summary>
internal readonly record struct CleanupResult(int Tokens, int Secrets)
{
    /// <summary>Было ли что убирать.</summary>
    public bool Any => Tokens + Secrets > 0;
}

/// <summary>Уборка мёртвых токенов и их секретов.</summary>
internal sealed class TokenCleanup(
    AppDbContext db,
    ISettingsReader settings,
    IAuditWriter audit,
    TimeProvider clock)
{
    public async Task<CleanupResult> RunAsync(CancellationToken cancellationToken = default)
    {
        var days = Math.Max(1, await settings.GetAsync(TokenSettings.RetentionDays, cancellationToken));
        var cutoff = clock.GetUtcNow().AddDays(-days);

        // ⚠ Ключи интеграции — своим сроком (ревью 2026-10-20, решение 0103).
        var keyDays = Math.Max(1, await settings.GetAsync(TokenSettings.KeepDeadDays, cancellationToken));
        var keyCutoff = clock.GetUtcNow().AddDays(-keyDays);

        return await db.InTransactionAsync(
            async (token) =>
            {
                var secrets = await db.Set<AuthTokenSecret>()
                    .Where(row => row.ExpiresAt < cutoff)
                    .ExecuteDeleteAsync(token);

                var tokens = await db.Set<AuthToken>()
                    .Where(row => row.Kind == TokenKinds.ApiKey
                        ? row.ExpiresAt < keyCutoff || (row.RevokedAt != null && row.RevokedAt < keyCutoff)
                        : row.ExpiresAt < cutoff || (row.RevokedAt != null && row.RevokedAt < cutoff))
                    .ExecuteDeleteAsync(token);

                var result = new CleanupResult(tokens, secrets);

                if (!result.Any)
                {
                    return result;
                }

                audit.Write(new AuditEntry(
                    ApiTokenActions.Cleaned,
                    ActorType: AuditActorType.System,
                    ResourceType: TokenResources.Session,
                    Changes: new Dictionary<string, object?>
                    {
                        ["sessions"] = tokens,
                        ["tokens"] = secrets,
                        ["older_than_days"] = days,
                    }));

                await db.SaveChangesAsync(token);

                return result;
            },
            cancellationToken);
    }
}
