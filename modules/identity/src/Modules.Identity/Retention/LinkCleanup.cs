using Modules.Identity.Settings;
using Modules.Identity.Contracts;
using Modules.Identity.Domain;
using Modules.Identity.Naming;
using Modules.Core.Contracts.Audit;
using Modules.Core.Contracts.Settings;
using Modules.Core.Data;
using Microsoft.EntityFrameworkCore;

namespace Modules.Identity.Retention;

/// <summary>Сколько чего убрано за один проход.</summary>
internal readonly record struct CleanupResult(int Links)
{
    /// <summary>Было ли что убирать.</summary>
    public bool Any => Links > 0;
}

/// <summary>Уборка одноразовых ссылок.</summary>
internal sealed class LinkCleanup(
    AppDbContext db,
    ISettingsReader settings,
    IAuditWriter audit,
    TimeProvider clock)
{
    public async Task<CleanupResult> RunAsync(CancellationToken cancellationToken = default)
    {
        var days = Math.Max(1, await settings.GetAsync(IdentitySettings.LinkRetentionDays, cancellationToken));
        var cutoff = clock.GetUtcNow().AddDays(-days);

        return await db.InTransactionAsync(
            async (token) =>
            {
                var links = await db.Set<IdentityToken>()
                    .Where(row => row.ExpiresAt < cutoff)
                    .ExecuteDeleteAsync(token);

                var result = new CleanupResult(links);

                if (!result.Any)
                {
                    return result;
                }

                audit.Write(new AuditEntry(
                    IdentityActions.LinksCleaned,
                    ActorType: AuditActorType.System,
                    ResourceType: IdentityResources.User,
                    Changes: new Dictionary<string, object?>
                    {
                        ["links"] = links,
                        ["older_than_days"] = days,
                    }));

                await db.SaveChangesAsync(token);

                return result;
            },
            cancellationToken);
    }
}
