using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using KerioCertManager.Emulators;
using KerioCertManager.Modules.Kerio.Certificates;
using KerioCertManager.Modules.Kerio.Client;
using KerioCertManager.Modules.Kerio.Domain;

namespace KerioCertManager.Modules.Kerio.Tests;

/// <summary>Куда сервис не ходит и чьим сертификатам на порту администрирования верит.</summary>
public sealed class AddressTrustTests
{
    [Theory]
    [InlineData("127.0.0.1", true)]
    [InlineData("::1", true)]
    [InlineData("169.254.169.254", true)]
    [InlineData("0.0.0.0", true)]
    [InlineData("::ffff:127.0.0.1", true)]
    [InlineData("fe80::1", true)]
    [InlineData("224.0.0.1", true)]
    [InlineData("192.168.1.10", false)]
    [InlineData("10.0.0.5", false)]
    [InlineData("8.8.8.8", false)]
    public void Служебные_адреса(string адрес, bool служебный) =>
        Api.ServersController.Служебный(System.Net.IPAddress.Parse(адрес)).ShouldBe(служебный);

    [Theory]
    [InlineData("mail.example.com", "mail.example.com", true)]
    [InlineData("MAIL.example.com", "mail.EXAMPLE.com", true)]
    [InlineData("*.example.com", "mail.example.com", true)]
    [InlineData("*.example.com", "a.b.example.com", false)]
    [InlineData("*.example.com", "example.com", false)]
    [InlineData("webmail.example.com", "mail.example.com", false)]
    public void Сертификат_имени_доверен_только_для_своего_имени(string сертификат, string узел, bool покрывает) =>
        Certificates.Fingerprints.NameCovers(сертификат, узел).ShouldBe(покрывает);
}

/// <summary>Доверие к сертификату Kerio: по соединению уходит пароль администратора.</summary>
public sealed class TrustTests
{
    private static readonly TestPki Pki = TestPki.LoadOrCreate(null);

    private static readonly X509Certificate2 Лист =
        Pki.Issue(["mail.example.com"], DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30));

    [Fact]
    public void Закреплённый_отпечаток_только_точный()
    {
        var свой = Fingerprints.Sha256(Лист);

        TlsTrust.Accept(TlsModes.Pin, свой, null, Лист, null, SslPolicyErrors.RemoteCertificateChainErrors).ShouldBeTrue(
            "закреплённому верим и без цепочки");
        TlsTrust.Accept(TlsModes.Pin, new string('A', 64), null, Лист, null, SslPolicyErrors.None).ShouldBeFalse(
            "даже доверенная цепочка не заменяет отпечаток");
        TlsTrust.Accept(TlsModes.Pin, null, null, Лист, null, SslPolicyErrors.None).ShouldBeFalse();
    }

    [Fact]
    public void Центр_сертификации_требует_цепочку_и_имя()
    {
        TlsTrust.Accept(TlsModes.Ca, null, null, Лист, null, SslPolicyErrors.None).ShouldBeTrue();
        TlsTrust.Accept(TlsModes.Ca, null, null, Лист, null, SslPolicyErrors.RemoteCertificateChainErrors).ShouldBeFalse();
        TlsTrust.Accept(TlsModes.Ca, null, null, Лист, null, SslPolicyErrors.RemoteCertificateNameMismatch).ShouldBeFalse(
            "адрес по IP без объявленного имени — отказ");
        TlsTrust.Accept(TlsModes.Ca, null, "mail.example.com", Лист, null, SslPolicyErrors.RemoteCertificateNameMismatch).ShouldBeTrue(
            "имя объявлено и совпало");
        TlsTrust.Accept(TlsModes.Ca, null, "other.example.com", Лист, null, SslPolicyErrors.RemoteCertificateNameMismatch).ShouldBeFalse();
        TlsTrust.Accept(TlsModes.Ca, null, "other.example.com", Лист, null, SslPolicyErrors.None).ShouldBeFalse(
            "объявленное имя проверяется и при совпадении с адресом");
    }

    [Fact]
    public void Без_сертификата_и_с_неизвестным_режимом_отказ()
    {
        TlsTrust.Accept(TlsModes.Ca, null, null, null, null, SslPolicyErrors.None).ShouldBeFalse();
        TlsTrust.Accept("none", null, null, Лист, null, SslPolicyErrors.None).ShouldBeFalse();
        TlsTrust.Accept(TlsModes.Ca, null, null, Лист, null, SslPolicyErrors.RemoteCertificateNotAvailable).ShouldBeFalse();
    }

    [Fact]
    public void Ставящийся_сертификат_доверен_сверх_режима()
    {
        var сервер = new KerioServer
        {
            Id = Guid.NewGuid(),
            Name = "k",
            BaseUrl = "https://k:4040",
            Username = "u",
            TlsMode = TlsModes.Pin,
            TlsPinSha256 = new string('B', 64),
        };

        var проверка = TlsTrust.For(сервер, [Fingerprints.Sha256(Лист)]);
        проверка(this, Лист, null, SslPolicyErrors.RemoteCertificateChainErrors).ShouldBeTrue();

        var безНего = TlsTrust.For(сервер);
        безНего(this, Лист, null, SslPolicyErrors.RemoteCertificateChainErrors).ShouldBeFalse();
    }

    /// <summary>⚠ В режиме УЦ свой сертификат минует цепочку, но не имя: ключ из KMP мог уйти и на другой узел.</summary>
    [Fact]
    public void Свой_сертификат_в_режиме_УЦ_проверяется_по_имени()
    {
        var сервер = new KerioServer
        {
            Id = Guid.NewGuid(),
            Name = "k",
            BaseUrl = "https://10.0.0.5:4040",
            Username = "u",
            TlsMode = TlsModes.Ca,
        };

        var свой = Fingerprints.Sha256(Лист);
        const SslPolicyErrors Всё = SslPolicyErrors.RemoteCertificateChainErrors | SslPolicyErrors.RemoteCertificateNameMismatch;

        TlsTrust.For(сервер, [свой])(this, Лист, null, SslPolicyErrors.RemoteCertificateChainErrors).ShouldBeTrue(
            "имя совпало с адресом, цепочки нет — свой");
        TlsTrust.For(сервер, [свой])(this, Лист, null, Всё).ShouldBeFalse("адрес по IP без объявленного имени");

        сервер.TlsServerName = "mail.example.com";
        TlsTrust.For(сервер, [свой])(this, Лист, null, Всё).ShouldBeTrue("объявленное имя совпало");

        сервер.TlsServerName = "other.example.com";
        TlsTrust.For(сервер, [свой])(this, Лист, null, Всё).ShouldBeFalse("тот же ключ на чужом имени");
    }

    [Theory]
    [InlineData("/api/kerio/servers/probe", true)]
    [InlineData("/api/kerio/servers/TEST", true)]
    [InlineData("/api/kerio/servers/0193a3c4-0000-7000-8000-000000000000/check", true)]
    [InlineData("/api/kerio/servers/0193a3c4-0000-7000-8000-000000000000/Check/", true)]
    [InlineData("/api/kerio/servers", false)]
    [InlineData("/api/kerio/servers/0193a3c4-0000-7000-8000-000000000000", false)]
    [InlineData("/api/kerio/servers/0193a3c4-0000-7000-8000-000000000000/deployments", false)]
    [InlineData("/api/kerio/overview", false)]
    public void Дорогие_пути_под_пределом(string путь, bool дорогой) =>
        KerioModule.ДорогойПуть(new Microsoft.AspNetCore.Http.PathString(путь)).ShouldBe(дорогой);
}
