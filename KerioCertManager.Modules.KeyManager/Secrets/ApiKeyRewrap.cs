using KerioCertManager.Modules.KeyManager.Domain;
using Modules.Crypto;
using Modules.Core.Data;
using Microsoft.EntityFrameworkCore;

namespace KerioCertManager.Modules.KeyManager.Secrets;

/// <summary>Перекладывает обёртку ключа данных ключа API под текущий мастер-ключ; ключ не открывается.</summary>
internal sealed class ApiKeyRewrap(AppDbContext db, ISecretEnvelope envelope) : IRewrapTarget
{
    public string Name => "keymanager_state";

    public Task<RewrapBatch> RewrapAsync(
        int batch, string? after = null, CancellationToken cancellationToken = default) =>
        db.InTransactionAsync(
            async token =>
            {
                if (after is not null)
                {
                    return new RewrapBatch(0, 0, null);
                }

                var строка = await db.Set<KeyManagerState>()
                    .AsNoTracking()
                    .FirstOrDefaultAsync(с => с.Id == KeyManagerState.SingletonId, token);

                if (строка is not { DataKey: { } было })
                {
                    return new RewrapBatch(строка is null ? 0 : 1, 0, null);
                }

                var ключ = new WrappedDataKey(было);

                if (!envelope.NeedsRewrap(ключ))
                {
                    return new RewrapBatch(1, 0, null);
                }

                var стало = envelope.Rewrap(ключ).Bytes;

                var затронуто = await db.Set<KeyManagerState>()
                    .Where(с => с.Id == KeyManagerState.SingletonId && с.DataKey == было)
                    .ExecuteUpdateAsync(set => set.SetProperty(с => с.DataKey, стало), token);

                return new RewrapBatch(1, затронуто, null);
            },
            cancellationToken);
}
