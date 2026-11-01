using Modules.Tokens.Domain;
using Modules.Core.Contracts.Tokens;
using Modules.Core.Data;
using Microsoft.EntityFrameworkCore;

namespace Modules.Tokens.Tokens;

/// <summary>Ставит отметку подтверждения на строку выдачи.</summary>
internal sealed class StepUpMarker(AppDbContext db) : IStepUpMarker
{
    public async Task<bool> MarkAsync(
        Guid tokenId, DateTimeOffset at, CancellationToken cancellationToken = default)
    {
        var помечено = await db.Set<AuthToken>()
            .Where(row => row.Id == tokenId && row.RevokedAt == null)
            .ExecuteUpdateAsync(set => set.SetProperty(row => row.StepUpAt, at), cancellationToken);

        return помечено == 1;
    }
}
