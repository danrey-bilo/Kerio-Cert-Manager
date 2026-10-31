using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using KerioCertManager.Emulators;
using KerioCertManager.Modules.Kerio.Certificates;
using KerioCertManager.Modules.Kerio.Client;
using KerioCertManager.Modules.Kerio.Domain;
using KerioCertManager.Modules.Kerio.Secrets;
using KerioCertManager.Modules.KeyManager.Contracts;
using Modules.Crypto;

namespace KerioCertManager.Modules.Kerio.Tests;

/// <summary>Эмулятор Key Manager Plus и двух серверов Kerio по настоящему HTTPS на свободных портах.</summary>
public sealed class Stand : IAsyncDisposable
{
    public const string Почта = "mail.demo.local";

    public const string Звёздочка = "*.demo.local";

    private Stand(EmulatorHost host, ISecretEnvelope envelope)
    {
        Host = host;
        Конверт = envelope;
        Пароли = new ServerPasswords(envelope);
        Соединитель = new KerioConnector(Пароли);
        Источник = new ИсточникСтенда(host.Kmp, host.Pki);
    }

    public EmulatorHost Host { get; }

    public KerioEmulator A => Host.Kerio[0];

    public KerioEmulator B => Host.Kerio[1];

    public ISecretEnvelope Конверт { get; }

    internal ServerPasswords Пароли { get; }

    internal KerioConnector Соединитель { get; }

    internal ИсточникСтенда Источник { get; }

    internal TlsInspector Tls { get; } = new();

    public static async Task<Stand> StartAsync(int daysLeftA = 12)
    {
        var host = EmulatorHost.Build(new EmulatorOptions
        {
            ControlPort = EmulatorHost.FreePort(),
            KmpPort = EmulatorHost.FreePort(),
            Kerio =
            [
                new KerioSpec("kerio-a", EmulatorHost.FreePort(), [EmulatorHost.FreePort()]),
                new KerioSpec("kerio-b", EmulatorHost.FreePort(), [EmulatorHost.FreePort()]),
            ],
            Certificates = [(Почта, daysLeftA), (Звёздочка, 200)],
            DataDirectory = null,
        });

        await host.StartAsync(TestContext.Current.CancellationToken);

        return new Stand(host, Envelope());
    }

    public static ISecretEnvelope Envelope() =>
        new SecretEnvelope(MasterKeyRing.FromKeys(new Dictionary<int, byte[]> { [1] = [.. Enumerable.Repeat((byte)0x42, MasterKeyRing.KeyBytes)] }));

    /// <summary>Строка сервера для эмулятора: закреплён отпечаток того, что подаётся сейчас.</summary>
    internal KerioServer Сервер(KerioEmulator kerio, string? commonName, string? password = null)
    {
        var сервер = new KerioServer
        {
            Id = Guid.CreateVersion7(),
            Name = kerio.Name,
            BaseUrl = $"https://localhost:{kerio.AdminPort}",
            Username = kerio.User,
            TlsMode = TlsModes.Pin,
            TlsPinSha256 = Fingerprints.Sha256(kerio.ServedCertificate),
            CommonName = commonName,
            VerifyPorts = [.. kerio.ServicePorts],
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };

        Пароли.Put(сервер, password ?? kerio.Password);

        return сервер;
    }

    public async ValueTask DisposeAsync() => await Host.DisposeAsync();
}

/// <summary>Источник сертификатов поверх эмулятора KMP — как настоящий: лист + цепочка, зашифрованный ключ и пароль.</summary>
internal sealed class ИсточникСтенда(KmpEmulator kmp, TestPki pki) : ICertificateSource
{
    /// <summary>Подменить ключ чужим — проверка «ключ не подходит».</summary>
    public bool ЧужойКлюч { get; set; }

    /// <summary>Отказ выгрузки.</summary>
    public string? Отказ { get; set; }

    public int Выгрузок { get; private set; }

    public Task<SourceRefresh> RefreshAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(new SourceRefresh(true, true, kmp.Entries.Count, null));

    public Task<IReadOnlyList<SourceCertificate>> ListAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<SourceCertificate>>([.. kmp.Entries.Select(Выпуск)]);

    public Task<SourceCertificate?> CurrentAsync(string commonName, CancellationToken cancellationToken = default) =>
        Task.FromResult(kmp.Entries
            .Where(в => string.Equals(в.CommonName, commonName, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(в => в.CreatedAt)
            .Select(Выпуск)
            .FirstOrDefault());

    public Task<CertificateMaterial> FetchAsync(string commonName, string serialNumber, CancellationToken cancellationToken = default)
    {
        Выгрузок++;

        if (Отказ is { } текст)
        {
            throw new CertificateSourceException(текст);
        }

        var выпуск = kmp.Entries.FirstOrDefault(в => в.CommonName == commonName && в.Serial == serialNumber)
            ?? throw new CertificateSourceException("нет такого выпуска");

        using var чужой = RSA.Create(2048);
        var ключ = (ЧужойКлюч ? чужой : выпуск.Certificate.GetRSAPrivateKey()!)
            .ExportEncryptedPkcs8PrivateKeyPem(
                выпуск.Passphrase, new PbeParameters(PbeEncryptionAlgorithm.Aes256Cbc, HashAlgorithmName.SHA256, 1000));

        return Task.FromResult(new CertificateMaterial(
            выпуск.CommonName,
            выпуск.Serial,
            [X509CertificateLoader.LoadCertificate(выпуск.Certificate.RawData).RawData, pki.Intermediate.RawData, pki.Root.RawData],
            Encoding.ASCII.GetBytes(ключ),
            выпуск.Passphrase));
    }

    public Task<IReadOnlyList<byte[]>> FetchCertificatesAsync(
        string commonName, string serialNumber, CancellationToken cancellationToken = default)
    {
        if (Отказ is { } текст)
        {
            throw new CertificateSourceException(текст);
        }

        var выпуск = kmp.Entries.FirstOrDefault(в => в.CommonName == commonName && в.Serial == serialNumber)
            ?? throw new CertificateSourceException("нет такого выпуска");

        return Task.FromResult<IReadOnlyList<byte[]>>([выпуск.Certificate.RawData, pki.Intermediate.RawData]);
    }

    private static SourceCertificate Выпуск(KmpEntry в) => new(
        в.CommonName, в.Serial, DateOnly.FromDateTime(в.Certificate.NotAfter.ToUniversalTime()), в.CreatedAt, DateTimeOffset.UtcNow);
}
