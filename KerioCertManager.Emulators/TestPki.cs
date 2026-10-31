using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace KerioCertManager.Emulators;

/// <summary>Учебный удостоверяющий центр стенда: корень, промежуточный, выпуск серверных сертификатов.</summary>
public sealed class TestPki
{
    private TestPki(X509Certificate2 root, X509Certificate2 intermediate, string? crlBaseUrl)
    {
        Root = root;
        Intermediate = intermediate;
        CrlBaseUrl = crlBaseUrl;
    }

    /// <summary>Откуда клиенты берут списки отзыва (<c>…/crl/root.crl</c>, <c>…/crl/issuing.crl</c>).</summary>
    public string? CrlBaseUrl { get; }

    /// <summary>Пустой список отзыва корня (для промежуточного), DER.</summary>
    public byte[] RootCrl() => Crl(Root);

    /// <summary>Пустой список отзыва промежуточного (для серверных), DER.</summary>
    public byte[] IssuingCrl() => Crl(Intermediate);

    private static byte[] Crl(X509Certificate2 издатель) =>
        new CertificateRevocationListBuilder().Build(
            издатель,
            new System.Numerics.BigInteger(DateTimeOffset.UtcNow.ToUnixTimeSeconds()),
            DateTimeOffset.UtcNow.AddDays(7),
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1,
            DateTimeOffset.UtcNow.AddMinutes(-5));

    private static X509Extension? Точка(string? база, string файл) =>
        база is null ? null : CertificateRevocationListBuilder.BuildCrlDistributionPointExtension([$"{база.TrimEnd('/')}/crl/{файл}"]);

    public X509Certificate2 Root { get; }

    public X509Certificate2 Intermediate { get; }

    /// <summary>Загрузить из каталога или создать заново.</summary>
    public static TestPki LoadOrCreate(string? directory, string? crlBaseUrl = null)
    {
        if (directory is not null)
        {
            Directory.CreateDirectory(directory);

            var кореньФайл = Path.Combine(directory, "root.pfx");
            var промФайл = Path.Combine(directory, "intermediate.pfx");

            if (File.Exists(кореньФайл) && File.Exists(промФайл))
            {
                return new TestPki(
                    X509CertificateLoader.LoadPkcs12FromFile(кореньФайл, null, X509KeyStorageFlags.Exportable),
                    X509CertificateLoader.LoadPkcs12FromFile(промФайл, null, X509KeyStorageFlags.Exportable),
                    crlBaseUrl);
            }

            var новый = Create(crlBaseUrl, метка: null);
            File.WriteAllBytes(кореньФайл, новый.Root.Export(X509ContentType.Pkcs12));
            File.WriteAllBytes(промФайл, новый.Intermediate.Export(X509ContentType.Pkcs12));

            return новый;
        }

        // ⚠ Одноразовой PKI — своё имя на каждый запуск.
        return Create(crlBaseUrl, Convert.ToHexString(RandomNumberGenerator.GetBytes(3)).ToLowerInvariant());
    }

    private static TestPki Create(string? crlBaseUrl, string? метка)
    {
        var своё = метка is null ? string.Empty : $" {метка}";

        using var ключКорня = RSA.Create(3072);
        var запросКорня = new CertificateRequest(
            $"CN=Kerio Cert Manager Demo Root CA{своё}, O=Kerio Cert Manager Demo", ключКорня, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        запросКорня.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        запросКорня.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));
        запросКорня.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(запросКорня.PublicKey, false));
        var корень = запросКорня.CreateSelfSigned(DateTimeOffset.UtcNow.AddYears(-2), DateTimeOffset.UtcNow.AddYears(10));

        using var ключПром = RSA.Create(2048);
        var запросПром = new CertificateRequest(
            $"CN=Kerio Cert Manager Demo Issuing CA{своё}, O=Kerio Cert Manager Demo", ключПром, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        запросПром.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, true, 0, true));
        запросПром.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));
        запросПром.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(запросПром.PublicKey, false));
        запросПром.CertificateExtensions.Add(X509AuthorityKeyIdentifierExtension.CreateFromCertificate(корень, true, false));

        if (Точка(crlBaseUrl, "root.crl") is { } точкаКорня)
        {
            запросПром.CertificateExtensions.Add(точкаКорня);
        }

        using var пром = запросПром.Create(корень, DateTimeOffset.UtcNow.AddYears(-2).AddHours(1), DateTimeOffset.UtcNow.AddYears(5), RandomNumberGenerator.GetBytes(12));

        return new TestPki(корень, пром.CopyWithPrivateKey(ключПром), crlBaseUrl);
    }

    /// <summary>Выпустить серверный сертификат RSA 2048 с именами в SAN.</summary>
    public X509Certificate2 Issue(IReadOnlyList<string> dnsNames, DateTimeOffset notBefore, DateTimeOffset notAfter, byte[]? serial = null)
    {
        ArgumentNullException.ThrowIfNull(dnsNames);

        using var ключ = RSA.Create(2048);
        var запрос = new CertificateRequest($"CN={dnsNames[0]}", ключ, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        запрос.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        запрос.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, true));
        запрос.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.1")], false));
        запрос.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(запрос.PublicKey, false));
        запрос.CertificateExtensions.Add(X509AuthorityKeyIdentifierExtension.CreateFromCertificate(Intermediate, true, false));

        if (Точка(CrlBaseUrl, "issuing.crl") is { } точка)
        {
            запрос.CertificateExtensions.Add(точка);
        }

        var san = new SubjectAlternativeNameBuilder();

        foreach (var имя in dnsNames)
        {
            if (System.Net.IPAddress.TryParse(имя, out var ip))
            {
                san.AddIpAddress(ip);
            }
            else
            {
                san.AddDnsName(имя);
            }
        }

        запрос.CertificateExtensions.Add(san.Build());

        var номер = serial ?? RandomNumberGenerator.GetBytes(16);
        номер[0] &= 0x7F;

        using var сертификат = запрос.Create(Intermediate, notBefore, notAfter, номер);

        return сертификат.CopyWithPrivateKey(ключ);
    }

    /// <summary>Самоподписанный «встроенный» сертификат, как у Kerio после установки.</summary>
    public static X509Certificate2 SelfSigned(string name)
    {
        using var ключ = RSA.Create(2048);
        var запрос = new CertificateRequest($"CN={name}, C=US", ключ, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var сертификат = запрос.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-30), DateTimeOffset.UtcNow.AddYears(1));

        return X509CertificateLoader.LoadPkcs12(сертификат.Export(X509ContentType.Pkcs12), null, X509KeyStorageFlags.Exportable);
    }

    /// <summary>Сертификат, пригодный для Kestrel (с ключом, экспортируемый — без этого Windows не отдаёт ключ SChannel).</summary>
    public static X509Certificate2 ForServer(X509Certificate2 withKey) =>
        X509CertificateLoader.LoadPkcs12(withKey.Export(X509ContentType.Pkcs12), null, X509KeyStorageFlags.Exportable);
}
