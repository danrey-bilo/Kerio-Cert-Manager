using System.Globalization;
using System.Security.Cryptography;
using Modules.Mail.Domain;
using Modules.Crypto;
using Modules.Core.Data;
using Modules.Core.Errors;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Modules.Mail.Sending;

/// <summary>Перекладывает обёртки ключей тел писем в очереди под действующую версию мастер-ключа.</summary>
internal sealed class MailOutboxRewrap(
    AppDbContext db, ISecretEnvelope envelope, ILogger<MailOutboxRewrap> log) : IRewrapTarget
{
    public string Name => "mail_outbox";

    public Task<RewrapBatch> RewrapAsync(
        int batch, string? after = null, CancellationToken cancellationToken = default) =>
        db.InTransactionAsync(
            async token =>
            {
                var от = Guid.TryParse(after, CultureInfo.InvariantCulture, out var разобрано)
                    ? разобрано
                    : Guid.Empty;

                var строки = await db.Set<MailOutbox>()
                    .Where(письмо => письмо.Id.CompareTo(от) > 0)
                    .OrderBy(письмо => письмо.Id)
                    .Take(Math.Max(1, batch))
                    .Select(письмо => new { письмо.Id, письмо.DataKey })
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

                    // ⚠ Одна испорченная обёртка не обрывает проход — урок живого прогона хранилищ.
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
                        log.LogError("mail_body_key_unreadable {MailId} {Error}", строка.Id, SafeError.Describe(error));

                        нечитаемых++;

                        continue;
                    }

                    // ⚠ УСЛОВНЫМ обновлением, а не присваиванием: строку могли убрать уборкой между чтением порции и записью.
                    var затронуто = await db.Set<MailOutbox>()
                        .Where(письмо => письмо.Id == строка.Id && письмо.DataKey == было)
                        .ExecuteUpdateAsync(set => set.SetProperty(письмо => письмо.DataKey, стало), token);

                    if (затронуто == 1)
                    {
                        переложено++;
                    }
                }

                var дальше = строки.Count < Math.Max(1, batch) ? null : строки[^1].Id.ToString();

                return new RewrapBatch(строки.Count, переложено, дальше, нечитаемых);
            },
            cancellationToken);
}
