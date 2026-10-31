using KerioCertManager.Modules.KeyManager.Client;
using KerioCertManager.Modules.KeyManager.Contracts;
using KerioCertManager.Modules.KeyManager.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Modules.Core.Data;
using Modules.Core.Errors;

namespace KerioCertManager.Modules.KeyManager.Source;

/// <summary>Key Manager Plus как источник сертификатов для установки на серверы.</summary>
internal sealed class CertificateSource(
    AppDbContext db,
    IKeyManagerConnector connector,
    CertificateSnapshots snapshots,
    TimeProvider clock,
    ILogger<CertificateSource> log) : ICertificateSource
{
    public async Task<SourceRefresh> RefreshAsync(CancellationToken cancellationToken = default)
    {
        var настройки = await connector.ReadAsync(cancellationToken);

        if (!настройки.Configured)
        {
            return SourceRefresh.NotConfigured;
        }

        try
        {
            using var клиент = await connector.OpenAsync(cancellationToken);

            var снимок = await snapshots.TakeAsync(клиент, cancellationToken);

            await ОтметитьAsync(снимок.Certificates.Count, null);

            return new SourceRefresh(true, true, снимок.Certificates.Count, null);
        }
        catch (Exception ошибка) when (!(ошибка is OperationCanceledException && cancellationToken.IsCancellationRequested))
        {
            var текст = Причина(ошибка);

            await ОтметитьAsync(null, текст);
            log.LogWarning("keymanager_poll_failed {Error}", текст);

            return new SourceRefresh(true, false, 0, текст);
        }
    }

    public async Task<IReadOnlyList<SourceCertificate>> ListAsync(CancellationToken cancellationToken = default) =>
        await CertificateSnapshots.Current(db)
            .AsNoTracking()
            .OrderBy(с => с.CommonName)
            .ThenByDescending(с => с.FirstSeenAt)
            .Select(с => new SourceCertificate(с.CommonName, с.SerialNumber, с.ExpiresOn, с.FirstSeenAt, с.LastSeenAt))
            .ToListAsync(cancellationToken);

    public async Task<SourceCertificate?> CurrentAsync(string commonName, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(commonName);

        var имя = commonName.Trim().ToLowerInvariant();

        var выпуски = await CertificateSnapshots.Current(db)
            .AsNoTracking()
            .Where(с => с.CommonName.ToLower() == имя)
            .Select(с => new SourceCertificate(с.CommonName, с.SerialNumber, с.ExpiresOn, с.FirstSeenAt, с.LastSeenAt))
            .ToListAsync(cancellationToken);

        return Текущий(выпуски);
    }

    /// <summary>Замеченный позже всех; при равенстве — дольше действующий, затем больший серийный.</summary>
    internal static SourceCertificate? Текущий(IEnumerable<SourceCertificate> выпуски) =>
        выпуски
            .OrderByDescending(с => с.FirstSeenAt)
            .ThenByDescending(с => с.ExpiresOn ?? DateOnly.MinValue)
            .ThenByDescending(с => с.SerialNumber, StringComparer.Ordinal)
            .FirstOrDefault();

    public async Task<CertificateMaterial> FetchAsync(
        string commonName, string serialNumber, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(commonName);
        ArgumentException.ThrowIfNullOrWhiteSpace(serialNumber);

        var выпуск = await CertificateSnapshots.Current(db)
            .AsNoTracking()
            .FirstOrDefaultAsync(с => с.CommonName == commonName && с.SerialNumber == serialNumber, cancellationToken)
            ?? throw new CertificateSourceException(
                $"Сертификата {commonName} с серийным номером {serialNumber} нет среди тех, что видел последний опрос "
                + "Key Manager Plus.");

        IKeyManagerClient клиент;

        try
        {
            клиент = await connector.OpenAsync(cancellationToken);
        }
        catch (Exception ошибка) when (ошибка is AppException or KeyManagerException)
        {
            throw new CertificateSourceException(Причина(ошибка), ошибка);
        }

        using (клиент)
        {
            try
            {
                var сертификаты = new List<byte[]>(
                    CertificateFiles.Ders((await клиент.DownloadAsync(
                        выпуск.CommonName, выпуск.SerialNumber, CertificateFormats.Pem, cancellationToken)).Content));

                if (сертификаты.Count <= 1)
                {
                    try
                    {
                        var p7b = await клиент.DownloadAsync(
                            выпуск.CommonName, выпуск.SerialNumber, CertificateFormats.P7b, cancellationToken);

                        сертификаты.AddRange(CertificateFiles.Ders(p7b.Content));
                    }
                    catch (KeyManagerException ошибка)
                    {
                        log.LogWarning(
                            "keymanager_chain_unavailable {CommonName} {SerialNumber} {Error}",
                            выпуск.CommonName, выпуск.SerialNumber, ошибка.Message);
                    }
                }

                if (сертификаты.Count == 0)
                {
                    throw new CertificateSourceException(
                        $"Key Manager Plus не отдал сертификат {выпуск.CommonName} в виде PEM или PKCS#7.");
                }

                var ключ = await клиент.DownloadAsync(
                    выпуск.CommonName, выпуск.SerialNumber, CertificateFormats.Key, cancellationToken);

                var пароль = await ПарольAsync(клиент, выпуск, cancellationToken);

                return new CertificateMaterial(
                    выпуск.CommonName,
                    выпуск.SerialNumber,
                    [.. сертификаты.DistinctBy(Convert.ToHexString)],
                    ключ.Content,
                    пароль);
            }
            catch (KeyManagerException ошибка)
            {
                throw new CertificateSourceException(ошибка.Message, ошибка);
            }
        }
    }

    public async Task<IReadOnlyList<byte[]>> FetchCertificatesAsync(
        string commonName, string serialNumber, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(commonName);
        ArgumentException.ThrowIfNullOrWhiteSpace(serialNumber);

        var выпуск = await CertificateSnapshots.Current(db)
            .AsNoTracking()
            .FirstOrDefaultAsync(с => с.CommonName == commonName && с.SerialNumber == serialNumber, cancellationToken)
            ?? throw new CertificateSourceException(
                $"Сертификата {commonName} с серийным номером {serialNumber} нет среди тех, что видел последний опрос "
                + "Key Manager Plus.");

        try
        {
            using var клиент = await connector.OpenAsync(cancellationToken);
            var pem = await клиент.DownloadAsync(выпуск.CommonName, выпуск.SerialNumber, CertificateFormats.Pem, cancellationToken);

            return [.. CertificateFiles.Ders(pem.Content)];
        }
        catch (Exception ошибка) when (ошибка is AppException or KeyManagerException)
        {
            throw new CertificateSourceException(Причина(ошибка), ошибка);
        }
    }

    private async Task<string?> ПарольAsync(IKeyManagerClient клиент, KnownCertificate выпуск, CancellationToken cancellationToken)
    {
        try
        {
            return await клиент.PassphraseAsync(выпуск.CommonName, выпуск.SerialNumber, cancellationToken);
        }
        catch (KeyManagerException ошибка)
        {
            log.LogWarning(
                "keymanager_passphrase_unavailable {CommonName} {SerialNumber} {Error}",
                выпуск.CommonName, выпуск.SerialNumber, ошибка.Message);

            return null;
        }
    }

    private static string Причина(Exception ошибка) => ошибка switch
    {
        KeyManagerException или => или.Message,
        AppException прил => прил.Message,
        _ => $"Опрос Key Manager Plus не удался: {SafeError.Describe(ошибка)}.",
    };

    /// <summary>Итог опроса в строку состояния; сбой записи — в лог, не наверх.</summary>
    private async Task ОтметитьAsync(int? сколько, string? ошибка)
    {
        try
        {
            await db.Database.ExecuteSqlAsync(
                $"""
                INSERT INTO keymanager_state (id, last_poll_at, last_poll_error, last_poll_count)
                VALUES ({KeyManagerState.SingletonId}, {clock.GetUtcNow()}, {ошибка}, {сколько})
                ON CONFLICT (id) DO UPDATE
                SET last_poll_at = EXCLUDED.last_poll_at,
                    last_poll_error = EXCLUDED.last_poll_error,
                    last_poll_count = COALESCE(EXCLUDED.last_poll_count, keymanager_state.last_poll_count)
                """,
                CancellationToken.None);
        }
        catch (Exception сбой)
        {
            log.LogError("keymanager_poll_mark_failed {Error}", SafeError.Describe(сбой));
        }
    }
}
