using System.Security.Cryptography;
using Modules.Mail.Domain;
using Modules.Crypto;
using Modules.Core.Data;
using Modules.Core.Errors;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Modules.Mail.Secrets;

/// <summary>Перекладывает обёртки ключей системных секретов под действующую версию мастер-ключа.</summary>
internal sealed class SystemSecretsRewrap(
    AppDbContext db, ISecretEnvelope envelope, ILogger<SystemSecretsRewrap> log) : IRewrapTarget
{
    public string Name => "system_secrets";

    public Task<RewrapBatch> RewrapAsync(
        int batch, string? after = null, CancellationToken cancellationToken = default) =>
        db.InTransactionAsync(
            async (token) =>
            {
                var от = after ?? string.Empty;

                var строки = await db.Set<SystemSecret>()
                    // ⚠ `string.Compare` БЕЗ `StringComparison`: перегрузку с ним EF в SQL не переводит и роняет запрос в рантайме.
                    .Where(с => string.Compare(с.Key, от) > 0)
                    .OrderBy(с => с.Key)
                    .Take(Math.Max(1, batch))
                    .AsNoTracking()
                    .ToListAsync(token);

                if (строки.Count == 0)
                {
                    return new RewrapBatch(0, 0, null);
                }

                var переложено = 0;
                var нечитаемых = 0;

                foreach (var строка in строки)
                {
                    var было = строка.DataKey;
                    var ключ = new WrappedDataKey(было);

                    byte[] стало;

                    // ⚠ ⚠ Одна испорченная обёртка не обрывает проход (ревью 2026-10-20, решение 0103).
                    try
                    {
                        if (!envelope.NeedsRewrap(ключ))
                        {
                            continue;
                        }

                        стало = envelope.Rewrap(ключ).Bytes;
                    }
                    catch (CryptographicException error)
                    {
                        log.LogError(
                            "system_secret_key_unreadable {Key} {Error}", строка.Key, SafeError.Describe(error));

                        нечитаемых++;

                        continue;
                    }

                    var затронуто = await db.Set<SystemSecret>()
                        .Where(с => с.Key == строка.Key && с.DataKey == было)
                        .ExecuteUpdateAsync(set => set.SetProperty(с => с.DataKey, стало), token);

                    if (затронуто == 1)
                    {
                        переложено++;
                    }
                }

                var дальше = строки.Count < Math.Max(1, batch) ? null : строки[^1].Key;

                return new RewrapBatch(строки.Count, переложено, дальше, нечитаемых);
            },
            cancellationToken);
}
