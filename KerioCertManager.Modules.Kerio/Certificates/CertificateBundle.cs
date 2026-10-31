using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using KerioCertManager.Modules.KeyManager.Contracts;
using Org.BouncyCastle.Crypto;
using Org.BouncyCastle.OpenSsl;
using Org.BouncyCastle.Pkcs;

namespace KerioCertManager.Modules.Kerio.Certificates;

/// <summary>Отказ сборки пары: ключ не открылся, не подходит, сертификат не тот или не годен.</summary>
internal sealed class BundleException(string message) : Exception(message);

/// <summary>Готовая к загрузке в Kerio пара: сертификат с цепочкой и открытый ключ PKCS#8.</summary>
internal sealed class CertificateBundle : IDisposable
{
    private CertificateBundle(
        X509Certificate2 leaf, IReadOnlyList<X509Certificate2> intermediates, byte[] privateKeyPem, bool chainComplete)
    {
        Leaf = leaf;
        Intermediates = intermediates;
        PrivateKeyPem = privateKeyPem;
        ChainComplete = chainComplete;
        Sha256 = Fingerprints.Sha256(leaf);
        Serial = Fingerprints.Serial(leaf.SerialNumber);
        CertificatePem = Encoding.ASCII.GetBytes(Fingerprints.Pem([leaf, .. intermediates]));
    }

    public X509Certificate2 Leaf { get; }

    /// <summary>Промежуточные по порядку от издателя листа вверх; корень не включается.</summary>
    public IReadOnlyList<X509Certificate2> Intermediates { get; }

    /// <summary>Цепочка дошла до самоподписанного корня среди присланного или до доверенного системой.</summary>
    public bool ChainComplete { get; }

    /// <summary>Лист и промежуточные в PEM — файл сертификата для Kerio.</summary>
    public byte[] CertificatePem { get; }

    /// <summary>Открытый ключ PKCS#8 в PEM — файл ключа для Kerio.</summary>
    public byte[] PrivateKeyPem { get; }

    public string Sha256 { get; }

    /// <summary>Серийный номер листа в нормальном виде.</summary>
    public string Serial { get; }

    public DateTimeOffset NotBefore => new(Leaf.NotBefore.ToUniversalTime(), TimeSpan.Zero);

    public DateTimeOffset NotAfter => new(Leaf.NotAfter.ToUniversalTime(), TimeSpan.Zero);

    public string Subject => Fingerprints.CommonName(Leaf.SubjectName);

    public string Issuer => Fingerprints.CommonName(Leaf.IssuerName);

    /// <summary>Собрать и проверить пару из выгрузки KMP.</summary>
    public static CertificateBundle Build(CertificateMaterial material, string commonName, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(material);
        ArgumentException.ThrowIfNullOrWhiteSpace(commonName);

        var сертификаты = new List<X509Certificate2>();

        foreach (var der in material.Certificates)
        {
            try
            {
                сертификаты.Add(X509CertificateLoader.LoadCertificate(der));
            }
            catch (CryptographicException)
            {
            }
        }

        if (сертификаты.Count == 0)
        {
            throw new BundleException("В файлах Key Manager Plus нет ни одного разбираемого сертификата.");
        }

        using var ключ = LoadKey(material.PrivateKey, material.Passphrase);
        var открытый = ключ.ExportSubjectPublicKeyInfo();

        var своиКлючу = сертификаты
            .Where(с => ОткрытыйКлюч(с) is { } spki && spki.AsSpan().SequenceEqual(открытый))
            .ToList();

        if (своиКлючу.Count == 0)
        {
            throw new BundleException(
                "Закрытый ключ из Key Manager Plus не подходит ни к одному присланному сертификату — файлы разных выпусков.");
        }

        // ⚠ Ключ мог пережить продление: тогда к нему подходит и прежний выпуск.
        var лист = своиКлючу.FirstOrDefault(с => Fingerprints.SameSerial(с.SerialNumber, material.SerialNumber))
            ?? throw new BundleException(
                $"В выгрузке Key Manager Plus нет сертификата с серийным номером {Fingerprints.Serial(material.SerialNumber)}: "
                + $"к ключу подходит только {string.Join(", ", своиКлючу.Select(с => Fingerprints.Serial(с.SerialNumber)))}.");

        if (!Fingerprints.Covers(лист, commonName))
        {
            throw new BundleException(
                $"Сертификат выдан на «{Fingerprints.CommonName(лист.SubjectName)}» и не покрывает имя {commonName}.");
        }

        if (лист.NotAfter.ToUniversalTime() <= now.UtcDateTime)
        {
            throw new BundleException($"Сертификат истёк {лист.NotAfter.ToUniversalTime():yyyy-MM-dd HH:mm} UTC.");
        }

        if (лист.NotBefore.ToUniversalTime() > now.UtcDateTime.AddMinutes(5))
        {
            throw new BundleException($"Сертификат начинает действовать только {лист.NotBefore.ToUniversalTime():yyyy-MM-dd HH:mm} UTC.");
        }

        var (промежуточные, полная) = Цепочка(лист, сертификаты);

        var буфер = new char[16 * 1024];
        byte[] pem;

        try
        {
            pem = ключ.TryExportPkcs8PrivateKeyPem(буфер, out var длина)
                ? Encoding.ASCII.GetBytes(буфер, 0, длина)
                : throw new BundleException("Закрытый ключ слишком велик.");
        }
        finally
        {
            Array.Clear(буфер);
        }

        return new CertificateBundle(лист, промежуточные, pem, полная);
    }

    /// <summary>Открыть закрытый ключ любого вида KMP: PKCS#1/PKCS#8/EC, зашифрованный PKCS#8 или PEM OpenSSL с Proc-Type.</summary>
    internal static AsymmetricAlgorithm LoadKey(byte[] file, string? passphrase)
    {
        ArgumentNullException.ThrowIfNull(file);

        var текст = Encoding.ASCII.GetChars(file);

        try
        {
            ReadOnlySpan<char> pem = текст;

            if (!pem.Contains("-----BEGIN", StringComparison.Ordinal))
            {
                return ИзDer(file, passphrase);
            }

            if (pem.Contains("Proc-Type: 4,ENCRYPTED", StringComparison.Ordinal))
            {
                return ИзOpenSsl(new string(текст), passphrase);
            }

            if (pem.Contains("-----BEGIN ENCRYPTED PRIVATE KEY-----", StringComparison.Ordinal))
            {
                if (string.IsNullOrEmpty(passphrase))
                {
                    throw new BundleException("Закрытый ключ зашифрован, а Key Manager Plus не отдал пароль к нему.");
                }

                return Попробовать(
                    () => { var r = RSA.Create(); r.ImportFromEncryptedPem(текст, passphrase); return r; },
                    () => { var e = ECDsa.Create(); e.ImportFromEncryptedPem(текст, passphrase); return e; })
                    ?? throw new BundleException("Закрытый ключ не открылся паролем из Key Manager Plus.");
            }

            return Попробовать(
                () => { var r = RSA.Create(); r.ImportFromPem(текст); return r; },
                () => { var e = ECDsa.Create(); e.ImportFromPem(текст); return e; })
                ?? throw new BundleException("Файл ключа из Key Manager Plus не разобрался как закрытый ключ RSA или EC.");
        }
        finally
        {
            Array.Clear(текст);
        }
    }

    private static AsymmetricAlgorithm ИзDer(byte[] der, string? пароль) =>
        Попробовать(
            () => { var r = RSA.Create(); r.ImportPkcs8PrivateKey(der, out _); return r; },
            () => { var e = ECDsa.Create(); e.ImportPkcs8PrivateKey(der, out _); return e; },
            () => { var r = RSA.Create(); r.ImportRSAPrivateKey(der, out _); return r; },
            () => { var r = RSA.Create(); r.ImportEncryptedPkcs8PrivateKey(пароль ?? string.Empty, der, out _); return r; },
            () => { var e = ECDsa.Create(); e.ImportEncryptedPkcs8PrivateKey(пароль ?? string.Empty, der, out _); return e; })
        ?? throw new BundleException("Файл ключа из Key Manager Plus не разобрался ни как PEM, ни как DER.");

    /// <summary>Устаревший PEM OpenSSL с шифрованием в заголовках: .NET его не открывает — через BouncyCastle.</summary>
    private static AsymmetricAlgorithm ИзOpenSsl(string текст, string? пароль)
    {
        if (string.IsNullOrEmpty(пароль))
        {
            throw new BundleException("Закрытый ключ зашифрован, а Key Manager Plus не отдал пароль к нему.");
        }

        object? прочитано;

        try
        {
            using var чтение = new StringReader(текст);
            прочитано = new PemReader(чтение, new Пароль(пароль)).ReadObject();
        }
        catch (Exception ошибка) when (ошибка is InvalidCipherTextException or PemException or IOException or ArgumentException)
        {
            throw new BundleException("Закрытый ключ не открылся паролем из Key Manager Plus.");
        }
        catch (OverflowException)
        {
            throw new BundleException("Пароль ключа содержит знаки вне ASCII — устаревший формат ключа OpenSSL так не шифруется.");
        }

        var закрытый = прочитано switch
        {
            AsymmetricCipherKeyPair пара => пара.Private,
            AsymmetricKeyParameter параметр when параметр.IsPrivate => параметр,
            _ => throw new BundleException("В файле ключа из Key Manager Plus нет закрытого ключа."),
        };

        var pkcs8 = PrivateKeyInfoFactory.CreatePrivateKeyInfo(закрытый).GetDerEncoded();

        try
        {
            return ИзDer(pkcs8, null);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(pkcs8);
        }
    }

    private static AsymmetricAlgorithm? Попробовать(params Func<AsymmetricAlgorithm>[] способы)
    {
        foreach (var способ in способы)
        {
            try
            {
                return способ();
            }
            catch (Exception ошибка) when (ошибка is CryptographicException or ArgumentException)
            {
            }
        }

        return null;
    }

    private static byte[]? ОткрытыйКлюч(X509Certificate2 сертификат)
    {
        using var rsa = сертификат.GetRSAPublicKey();

        if (rsa is not null)
        {
            return rsa.ExportSubjectPublicKeyInfo();
        }

        using var ec = сертификат.GetECDsaPublicKey();

        return ec?.ExportSubjectPublicKeyInfo();
    }

    /// <summary>Промежуточные от издателя листа вверх по присланному; корень не включается.</summary>
    private static (IReadOnlyList<X509Certificate2> Промежуточные, bool Полная) Цепочка(
        X509Certificate2 лист, IReadOnlyList<X509Certificate2> все)
    {
        var цепочка = new List<X509Certificate2>();
        var текущий = лист;

        for (var шаг = 0; шаг < 8; шаг++)
        {
            if (Самоподписан(текущий))
            {
                return (цепочка, true);
            }

            var издатель = все.FirstOrDefault(с =>
                !ReferenceEquals(с, текущий)
                && !цепочка.Contains(с)
                && с.SubjectName.RawData.AsSpan().SequenceEqual(текущий.IssuerName.RawData)
                && Подписал(с, текущий));

            if (издатель is null)
            {
                return (цепочка, ИздательВСистеме(текущий));
            }

            if (Самоподписан(издатель))
            {
                return (цепочка, true);
            }

            цепочка.Add(издатель);
            текущий = издатель;
        }

        return (цепочка, false);
    }

    private static bool Самоподписан(X509Certificate2 с) =>
        с.SubjectName.RawData.AsSpan().SequenceEqual(с.IssuerName.RawData) && Подписал(с, с);

    /// <summary>Издатель ли <paramref name="издатель"/> для <paramref name="ребёнок"/>: имя уже совпало, сверяем ключи (AKI/SKI).</summary>
    private static bool Подписал(X509Certificate2 издатель, X509Certificate2 ребёнок)
    {
        var aki = ребёнок.Extensions.OfType<X509AuthorityKeyIdentifierExtension>().FirstOrDefault()?.KeyIdentifier;
        var ski = издатель.Extensions.OfType<X509SubjectKeyIdentifierExtension>().FirstOrDefault()?.SubjectKeyIdentifierBytes;

        return aki is not { } а || ski is not { } б || а.Span.SequenceEqual(б.Span);
    }

    private static bool ИздательВСистеме(X509Certificate2 с)
    {
        try
        {
            using var цепь = new X509Chain();
            цепь.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
            цепь.ChainPolicy.VerificationFlags = X509VerificationFlags.IgnoreNotTimeValid;

            return цепь.Build(с);
        }
        catch (CryptographicException)
        {
            return false;
        }
    }

    public void Dispose()
    {
        CryptographicOperations.ZeroMemory(PrivateKeyPem);
        Leaf.Dispose();

        foreach (var с in Intermediates)
        {
            с.Dispose();
        }
    }

    /// <summary>Без содержимого.</summary>
    public override string ToString() => $"{nameof(CertificateBundle)} {Subject} #{Serial} до {NotAfter:yyyy-MM-dd}";

    private sealed class Пароль(string пароль) : IPasswordFinder
    {
        public char[] GetPassword() => пароль.ToCharArray();
    }
}
