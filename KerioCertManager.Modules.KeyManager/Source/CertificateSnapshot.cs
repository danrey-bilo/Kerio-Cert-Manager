using KerioCertManager.Modules.KeyManager.Client;
using KerioCertManager.Modules.KeyManager.Domain;
using Modules.Core.Data;
using Microsoft.EntityFrameworkCore;

namespace KerioCertManager.Modules.KeyManager.Source;

/// <summary>Запросить список у Key Manager Plus, запомнить его и вернуть снимок.</summary>
internal sealed class CertificateSnapshots(AppDbContext db, TimeProvider clock)
{
    /// <summary>Сколько помнить пропавшую пару: не ноль, иначе сбой списка сделал бы её «новой».</summary>
    internal static readonly TimeSpan Хранить = TimeSpan.FromDays(30);

    /// <summary>Пары из последнего удачного опроса (наибольшее <c>last_seen_at</c>).</summary>
    public static IQueryable<KnownCertificate> Current(AppDbContext db) =>
        db.Set<KnownCertificate>().Where(с =>
            с.LastSeenAt >= db.Set<KnownCertificate>().Max(в => в.LastSeenAt));

    /// <summary>Запросить список и запомнить его; пустой ответ снимок не трогает. Вернуть, сколько сертификатов отдал KMP.</summary>
    public async Task<int> TakeAsync(IKeyManagerClient client, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(client);

        // ⚠ Время — до запроса: отправленное позже снимок мог не увидеть.
        var когда = clock.GetUtcNow();
        var список = await client.ListAsync(cancellationToken);

        if (список.Count > 0)
        {
            await ЗапомнитьAsync(список, когда, cancellationToken);
        }

        return список.Count;
    }

    /// <summary>Новые пары добавить, виденные — отметить.</summary>
    private Task ЗапомнитьAsync(IReadOnlyList<KmpCertificate> список, DateTimeOffset когда, CancellationToken cancellationToken) =>
        db.InTransactionAsync(
            async token =>
            {
                foreach (var с in список.DistinctBy(с => (с.CommonName, с.SerialNumber)))
                {
                    await db.Database.ExecuteSqlAsync(
                        $"""
                        INSERT INTO keymanager_certificates
                            (common_name, serial_number, expires_on, first_seen_at, last_seen_at)
                        VALUES ({с.CommonName}, {с.SerialNumber}, {с.ExpiresOn}, {когда}, {когда})
                        ON CONFLICT (common_name, serial_number) DO UPDATE
                        SET expires_on = EXCLUDED.expires_on,
                            last_seen_at = GREATEST(keymanager_certificates.last_seen_at, EXCLUDED.last_seen_at)
                        """,
                        token);
                }

                // ⚠ Забывать — после отметки увиденных: иначе долгий перерыв в опросе стёр бы и живые пары.
                var давно = когда - Хранить;
                await db.Set<KnownCertificate>()
                    .Where(с => с.LastSeenAt < давно)
                    .ExecuteDeleteAsync(token);
            },
            cancellationToken);
}
