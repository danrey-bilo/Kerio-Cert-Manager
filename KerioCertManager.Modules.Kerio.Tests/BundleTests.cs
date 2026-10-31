using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using KerioCertManager.Emulators;
using KerioCertManager.Modules.Kerio.Certificates;
using KerioCertManager.Modules.KeyManager.Contracts;
using Org.BouncyCastle.OpenSsl;
using Org.BouncyCastle.Security;

namespace KerioCertManager.Modules.Kerio.Tests;

/// <summary>Пара для Kerio: ключ любого вида KMP, сверка с сертификатом, имя, срок, цепочка.</summary>
public sealed class BundleTests
{
    private static readonly TestPki Pki = TestPki.LoadOrCreate(null);

    private static readonly DateTimeOffset Сейчас = DateTimeOffset.UtcNow;

    [Fact]
    public void Пара_собирается_с_цепочкой_без_корня_и_открытым_ключом_PKCS8()
    {
        using var лист = Выпуск("mail.example.com");
        using var пара = CertificateBundle.Build(Материал(лист, Зашифрованный(лист, "пароль"), "пароль"), "mail.example.com", Сейчас);

        пара.Intermediates.Count.ShouldBe(1);
        пара.Intermediates[0].Subject.ShouldContain("Issuing CA");
        пара.ChainComplete.ShouldBeTrue();
        пара.Sha256.ShouldBe(Fingerprints.Sha256(лист));
        пара.Serial.ShouldBe(Fingerprints.Serial(лист.SerialNumber));
        Encoding.ASCII.GetString(пара.PrivateKeyPem).ShouldStartWith("-----BEGIN PRIVATE KEY-----");

        var pem = Encoding.ASCII.GetString(пара.CertificatePem);
        pem.Split("-----BEGIN CERTIFICATE-----").Length.ShouldBe(3, "лист и промежуточный, корня нет");
        pem.ShouldStartWith(лист.ExportCertificatePem());
    }

    [Fact]
    public void Неверный_пароль_ключа_отказ()
    {
        using var лист = Выпуск("mail.example.com");

        Should.Throw<BundleException>(() => CertificateBundle.Build(Материал(лист, Зашифрованный(лист, "верный"), "другой"), "mail.example.com", Сейчас))
            .Message.ShouldContain("не открылся");
    }

    [Fact]
    public void Зашифрованный_ключ_без_пароля_отказ()
    {
        using var лист = Выпуск("mail.example.com");

        Should.Throw<BundleException>(() => CertificateBundle.Build(Материал(лист, Зашифрованный(лист, "x"), null), "mail.example.com", Сейчас))
            .Message.ShouldContain("не отдал пароль");
    }

    [Fact]
    public void Ключ_от_другого_выпуска_отказ()
    {
        using var лист = Выпуск("mail.example.com");
        using var чужой = Выпуск("mail.example.com");

        Should.Throw<BundleException>(() => CertificateBundle.Build(Материал(лист, Открытый(чужой), null), "mail.example.com", Сейчас))
            .Message.ShouldContain("не подходит");
    }

    [Fact]
    public void Сертификат_на_другое_имя_отказ()
    {
        using var лист = Выпуск("vpn.example.com");

        Should.Throw<BundleException>(() => CertificateBundle.Build(Материал(лист, Открытый(лист), null), "mail.example.com", Сейчас))
            .Message.ShouldContain("не покрывает");
    }

    [Fact]
    public void Истёкший_отказ()
    {
        using var лист = Pki.Issue(["mail.example.com"], Сейчас.AddDays(-100), Сейчас.AddDays(-1));

        Should.Throw<BundleException>(() => CertificateBundle.Build(Материал(лист, Открытый(лист), null), "mail.example.com", Сейчас))
            .Message.ShouldContain("истёк");
    }

    /// <summary>⚠ Устаревший PEM OpenSSL с «Proc-Type: 4,ENCRYPTED» .NET не открывает — только через BouncyCastle.</summary>
    [Fact]
    public void Устаревший_зашифрованный_PEM_OpenSSL_открывается()
    {
        using var лист = Выпуск("mail.example.com");
        var ключ = PrivateKeyFactory.CreateKey(лист.GetRSAPrivateKey()!.ExportPkcs8PrivateKey());

        using var текст = new StringWriter();
        var перо = new PemWriter(текст);
        перо.WriteObject(ключ, "AES-256-CBC", "old-pass-1".ToCharArray(), new SecureRandom());
        перо.Writer.Flush();

        текст.ToString().ShouldContain("Proc-Type: 4,ENCRYPTED");

        using var пара = CertificateBundle.Build(
            Материал(лист, Encoding.ASCII.GetBytes(текст.ToString()), "old-pass-1"), "mail.example.com", Сейчас);

        пара.Sha256.ShouldBe(Fingerprints.Sha256(лист));
    }

    [Fact]
    public void Открытый_PKCS1_и_EC_ключи_понимаются()
    {
        using var лист = Выпуск("mail.example.com");
        var pkcs1 = Encoding.ASCII.GetBytes(лист.GetRSAPrivateKey()!.ExportRSAPrivateKeyPem());

        using (var пара = CertificateBundle.Build(Материал(лист, pkcs1, null), "mail.example.com", Сейчас))
        {
            пара.Leaf.Thumbprint.ShouldBe(лист.Thumbprint);
        }

        using var ec = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var запрос = new CertificateRequest("CN=ec.example.com", ec, HashAlgorithmName.SHA256);
        using var ecЛист = запрос.CreateSelfSigned(Сейчас.AddDays(-1), Сейчас.AddDays(30));

        using var ecПара = CertificateBundle.Build(
            new CertificateMaterial("ec.example.com", ecЛист.SerialNumber, [ecЛист.RawData], Encoding.ASCII.GetBytes(ec.ExportECPrivateKeyPem()), null),
            "ec.example.com",
            Сейчас);

        ecПара.Intermediates.ShouldBeEmpty();
        ecПара.ChainComplete.ShouldBeTrue("самоподписанный — сам себе корень");
    }

    /// <summary>⚠ Ключ пережил продление: к нему подходит и прежний выпуск — ставится ровно запрошенный.</summary>
    [Fact]
    public void Ставится_запрошенный_выпуск_а_не_прежний_с_тем_же_ключом()
    {
        using var ключ = RSA.Create(2048);
        var прежний = new CertificateRequest("CN=mail.example.com", ключ, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var san = new SubjectAlternativeNameBuilder();
        san.AddDnsName("mail.example.com");
        прежний.CertificateExtensions.Add(san.Build());
        using var старый = прежний.Create(Pki.Intermediate, Сейчас.AddDays(-100), Сейчас.AddDays(30), [0x11, 0x01]);
        using var новый = прежний.Create(Pki.Intermediate, Сейчас.AddDays(-1), Сейчас.AddDays(90), [0x22, 0x02]);
        var pem = Encoding.ASCII.GetBytes(ключ.ExportPkcs8PrivateKeyPem());

        using (var пара = CertificateBundle.Build(
            new CertificateMaterial("mail.example.com", новый.SerialNumber, [старый.RawData, новый.RawData], pem, null),
            "mail.example.com",
            Сейчас))
        {
            пара.Leaf.SerialNumber.ShouldBe(новый.SerialNumber);
        }

        Should.Throw<BundleException>(() => CertificateBundle.Build(
                new CertificateMaterial("mail.example.com", "3303", [старый.RawData, новый.RawData], pem, null),
                "mail.example.com",
                Сейчас))
            .Message.ShouldContain("3303");
    }

    [Fact]
    public void Обнуление_ключа_при_освобождении()
    {
        using var лист = Выпуск("mail.example.com");
        var пара = CertificateBundle.Build(Материал(лист, Открытый(лист), null), "mail.example.com", Сейчас);
        var ключ = пара.PrivateKeyPem;

        пара.Dispose();

        ключ.ShouldAllBe(b => b == 0);
    }

    [Theory]
    [InlineData("*.demo.local", "*.demo.local", true)]
    [InlineData("*.demo.local", "mail.demo.local", true)]
    [InlineData("*.demo.local", "a.b.demo.local", false)]
    [InlineData("*.demo.local", "demo.local", false)]
    [InlineData("mail.demo.local", "MAIL.demo.local", true)]
    [InlineData("mail.demo.local", "*.demo.local", false)]
    public void Покрытие_имени(string вСертификате, string искомое, bool покрывает)
    {
        using var лист = Pki.Issue([вСертификате], Сейчас.AddDays(-1), Сейчас.AddDays(30));

        Fingerprints.Covers(лист, искомое).ShouldBe(покрывает);
    }

    [Theory]
    [InlineData("aa:BB:cc", null)]
    [InlineData("97:F1:CF:64:3F:FA:D3:B6:58:D6:D4:AB:15:1F:2C:83:B3:55:CF:AF:DA:FC:79:CC:2B:76:4C:EA:28:3F:39:E3", "97F1CF643FFAD3B658D6D4AB151F2C83B355CFAFDAFC79CC2B764CEA283F39E3")]
    [InlineData(" 97f1cf643ffad3b658d6d4ab151f2c83b355cfafdafc79cc2b764cea283f39e3 ", "97F1CF643FFAD3B658D6D4AB151F2C83B355CFAFDAFC79CC2B764CEA283F39E3")]
    public void Отпечаток_из_ввода_человека(string ввод, string? итог) => Fingerprints.NormalizePin(ввод).ShouldBe(итог);

    [Theory]
    [InlineData("00:0A:1b", "a1b", true)]
    [InlineData("0A1B", "a1c", false)]
    [InlineData("", "", false)]
    public void Серийный_в_одном_виде(string а, string б, bool тот) => Fingerprints.SameSerial(а, б).ShouldBe(тот);

    private static X509Certificate2 Выпуск(string имя) => Pki.Issue([имя], Сейчас.AddDays(-1), Сейчас.AddDays(60));

    private static byte[] Зашифрованный(X509Certificate2 лист, string пароль) => Encoding.ASCII.GetBytes(
        лист.GetRSAPrivateKey()!.ExportEncryptedPkcs8PrivateKeyPem(
            пароль, new PbeParameters(PbeEncryptionAlgorithm.Aes256Cbc, HashAlgorithmName.SHA256, 1000)));

    private static byte[] Открытый(X509Certificate2 лист) => Encoding.ASCII.GetBytes(лист.GetRSAPrivateKey()!.ExportPkcs8PrivateKeyPem());

    private static CertificateMaterial Материал(X509Certificate2 лист, byte[] ключ, string? пароль) => new(
        Fingerprints.CommonName(лист.SubjectName),
        лист.SerialNumber,
        [лист.RawData, Pki.Intermediate.RawData, Pki.Root.RawData],
        ключ,
        пароль);
}
