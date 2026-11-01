using System.Globalization;
using System.Security.Cryptography;
using Modules.Identity.Domain;
using Modules.Crypto;
using Modules.Core.Data;
using Modules.Core.Errors;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Modules.Identity.TwoFactor;

/// <summary>Перекладывает обёртки ключей второго фактора под действующую версию мастер-ключа.</summary>
internal sealed class TwoFactorRewrap(
    AppDbContext db, ISecretEnvelope envelope, ILogger<TwoFactorRewrap> log) : IRewrapTarget
{
    public string Name => "user_two_factor";

    public Task<RewrapBatch> RewrapAsync(
        int batch, string? after = null, CancellationToken cancellationToken = default) =>
        db.InTransactionAsync(
            async (token) =>
            {
                var from = Guid.TryParse(after, CultureInfo.InvariantCulture, out var parsed)
                    ? parsed
                    : Guid.Empty;

                var rows = await db.Set<UserTwoFactor>()
                    .Where(row => row.UserId.CompareTo(from) > 0)
                    .OrderBy(row => row.UserId)
                    .Take(Math.Max(1, batch))
                    .AsNoTracking()
                    .ToListAsync(token);

                if (rows.Count == 0)
                {
                    return new RewrapBatch(0, 0, null);
                }

                var moved = 0;
                var нечитаемых = 0;

                foreach (var row in rows)
                {
                    var было = row.DataKey;
                    var key = new WrappedDataKey(было);

                    // ⚠ Уже под действующей версией — не трогаем.
                    byte[] стало;

                    // ⚠ ⚠ Одна испорченная обёртка не обрывает проход (ревью 2026-10-20, решение 0103).
                    try
                    {
                        if (!envelope.NeedsRewrap(key))
                        {
                            continue;
                        }

                        стало = envelope.Rewrap(key).Bytes;
                    }
                    catch (CryptographicException error)
                    {
                        log.LogError(
                            "two_factor_key_unreadable {UserId} {Error}", row.UserId, SafeError.Describe(error));

                        нечитаемых++;

                        continue;
                    }

                    // ⚠ Условным обновлением, а не присваиванием. Между чтением порции и записью человек мог перезавести себе фактор.
                    var затронуто = await db.Set<UserTwoFactor>()
                        .Where(candidate => candidate.UserId == row.UserId && candidate.DataKey == было)
                        .ExecuteUpdateAsync(
                            set => set.SetProperty(candidate => candidate.DataKey, стало), token);

                    if (затронуто == 1)
                    {
                        moved++;
                    }
                }

                var next = rows.Count < Math.Max(1, batch)
                    ? null
                    : rows[^1].UserId.ToString();

                return new RewrapBatch(rows.Count, moved, next, нечитаемых);
            },
            cancellationToken);
}
