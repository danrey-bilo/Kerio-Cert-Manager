using System.IO.Compression;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using KerioCertManager.Modules.KeyManager.Client;
using KerioCertManager.Modules.KeyManager.Contracts;
using KerioCertManager.Modules.KeyManager.Domain;
using KerioCertManager.Modules.KeyManager.Source;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Modules.Core.Data;

namespace KerioCertManager.Modules.KeyManager.Tests;

/// <summary>Key Manager Plus как источник: опрос, текущий выпуск, выгрузка пары с цепочкой.</summary>
[Collection(KeyManagerDatabaseCollection.Name)]
public sealed class SourceTests(KeyManagerDatabase база) : IAsyncLifetime
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public ValueTask InitializeAsync()
    {
        база.Сброс();
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        await using var scope = база.Scope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await db.Set<KnownCertificate>().ExecuteDeleteAsync(CancellationToken.None);
        await db.Set<KeyManagerState>().ExecuteDeleteAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Опрос_запоминает_снимок_и_итог()
    {
        база.Kmp.Сертификаты.Add(new KmpCertificate("mail.example.com", "AA01", new DateOnly(2027, 1, 1)));
        база.Kmp.Сертификаты.Add(new KmpCertificate("vpn.example.com", "BB02", null));

        var итог = await Источник(i => i.RefreshAsync(Ct));

        итог.ShouldBe(new SourceRefresh(true, true, 2, null));

        var список = await Источник(i => i.ListAsync(Ct));
        список.Select(с => с.CommonName).ShouldBe(["mail.example.com", "vpn.example.com"]);

        await using var scope = база.Scope();
        var состояние = await scope.ServiceProvider.GetRequiredService<AppDbContext>().Set<KeyManagerState>().SingleAsync(Ct);
        состояние.LastPollCount.ShouldBe(2);
        состояние.LastPollError.ShouldBeNull();
    }

    [Fact]
    public async Task Неудачный_опрос_не_роняет_и_пишет_причину()
    {
        база.Kmp.СписокПадает = true;

        var итог = await Источник(i => i.RefreshAsync(Ct));

        итог.Succeeded.ShouldBeFalse();
        итог.Configured.ShouldBeTrue();
        итог.Error.ShouldNotBeNull().ShouldContain("недоступен");
    }

    /// <summary>⚠ KMP вдруг отдал пусто при известных сертификатах: это сбой, а не «все пропали» — снимок остаётся.</summary>
    [Fact]
    public async Task Пустой_ответ_при_известных_сертификатах_сбой()
    {
        база.Kmp.Сертификаты.Add(new KmpCertificate("mail.example.com", "AA01", new DateOnly(2027, 1, 1)));
        await Источник(i => i.RefreshAsync(Ct));

        база.Kmp.Сертификаты.Clear();
        var итог = await Источник(i => i.RefreshAsync(Ct));

        итог.Succeeded.ShouldBeFalse();
        итог.Error.ShouldNotBeNullOrEmpty();
        (await Источник(i => i.ListAsync(Ct))).ShouldHaveSingleItem().CommonName.ShouldBe("mail.example.com");
    }

    /// <summary>⚠ Выключенный источник ничего не отдаёт на установку, даже из старого снимка.</summary>
    [Fact]
    public async Task Выключенный_источник_не_выгружает()
    {
        база.Kmp.Сертификаты.Add(new KmpCertificate("mail.example.com", "AA01", new DateOnly(2027, 1, 1)));
        await Источник(i => i.RefreshAsync(Ct));
        (await Источник(i => i.AvailableAsync(Ct))).ShouldBeTrue();

        база.Kmp.Настройки = new KeyManagerConfiguration(false, "https://kmp.example.test:6565", true);

        (await Источник(i => i.AvailableAsync(Ct))).ShouldBeFalse();
        await Should.ThrowAsync<CertificateSourceException>(() => Источник(i => i.FetchAsync("mail.example.com", "AA01", Ct)));
    }

    [Fact]
    public async Task Не_настроено_значит_опроса_нет()
    {
        база.Kmp.Настройки = new KeyManagerConfiguration(false, "https://kmp.example.test:6565", true);

        (await Источник(i => i.RefreshAsync(Ct))).ShouldBe(SourceRefresh.NotConfigured);
        база.Kmp.Списков.ShouldBe(0);
    }

    /// <summary>⚠ Текущий — замеченный позже всех, а не самый долгий: перевыпуск бывает короче.</summary>
    [Fact]
    public async Task Текущий_выпуск_замеченный_позже_всех_без_учёта_регистра()
    {
        база.Kmp.Сертификаты.Add(new KmpCertificate("Mail.Example.com", "AA01", new DateOnly(2027, 6, 1)));
        await Источник(i => i.RefreshAsync(Ct));

        база.Часы.Вперёд(TimeSpan.FromHours(1));
        база.Kmp.Сертификаты.Add(new KmpCertificate("Mail.Example.com", "CC03", new DateOnly(2027, 3, 1)));
        await Источник(i => i.RefreshAsync(Ct));

        var текущий = await Источник(i => i.CurrentAsync("mail.example.com", Ct));

        текущий.ShouldNotBeNull().SerialNumber.ShouldBe("CC03");
        (await Источник(i => i.CurrentAsync("other.example.com", Ct))).ShouldBeNull();
    }

    [Fact]
    public async Task Выгрузка_даёт_сертификаты_ключ_и_пароль_и_берёт_цепочку_из_P7B()
    {
        var (лист, цепь) = Цепочка("mail.example.com");
        база.Kmp.Сертификаты.Add(new KmpCertificate("mail.example.com", "AA01", null));
        база.Kmp.Файлы[CertificateFormats.Pem] = Encoding.ASCII.GetBytes(лист.ExportCertificatePem());
        база.Kmp.Файлы[CertificateFormats.P7b] = P7b(лист, цепь);
        база.Kmp.Файлы[CertificateFormats.Key] = Encoding.ASCII.GetBytes("-----BEGIN PRIVATE KEY-----\nAA==\n-----END PRIVATE KEY-----\n");
        база.Kmp.Пароли["AA01"] = "секрет";

        await Источник(i => i.RefreshAsync(Ct));

        using var выгрузка = await Источник(i => i.FetchAsync("mail.example.com", "AA01", Ct));

        выгрузка.Certificates.Count.ShouldBe(2, "лист из PEM и промежуточный из P7B, без повторов");
        выгрузка.Certificates.Any(der => der.SequenceEqual(цепь.RawData)).ShouldBeTrue();
        выгрузка.Passphrase.ShouldBe("секрет");
        выгрузка.ToString().ShouldNotContain("секрет");
    }

    [Fact]
    public async Task Выгрузка_только_того_что_видел_опрос()
    {
        var ошибка = await Should.ThrowAsync<CertificateSourceException>(
            () => Источник(i => i.FetchAsync("mail.example.com", "FF", Ct)));

        ошибка.Message.ShouldContain("последний опрос");
        база.Kmp.Выгрузок.ShouldBe(0);
    }

    [Fact]
    public async Task Отказ_KMP_в_ключе_становится_отказом_источника()
    {
        база.Kmp.Сертификаты.Add(new KmpCertificate("mail.example.com", "AA01", null));
        база.Kmp.ПадатьНаФормате = CertificateFormats.Key;
        await Источник(i => i.RefreshAsync(Ct));

        var (лист, _) = Цепочка("mail.example.com");
        база.Kmp.Файлы[CertificateFormats.Pem] = Encoding.ASCII.GetBytes(лист.ExportCertificatePem());

        await Should.ThrowAsync<CertificateSourceException>(() => Источник(i => i.FetchAsync("mail.example.com", "AA01", Ct)));
    }

    [Fact]
    public void Разбор_файлов_понимает_PEM_цепочку_DER_P7B_и_ZIP()
    {
        var (лист, цепь) = Цепочка("x.example.com");
        var pem = Encoding.ASCII.GetBytes(лист.ExportCertificatePem() + "\n" + цепь.ExportCertificatePem());

        CertificateFiles.Ders(pem).Count.ShouldBe(2);
        CertificateFiles.Ders(лист.RawData).Count.ShouldBe(1);
        CertificateFiles.Ders(P7b(лист, цепь)).Count.ShouldBe(2);

        using var буфер = new MemoryStream();

        using (var архив = new ZipArchive(буфер, ZipArchiveMode.Create, leaveOpen: true))
        {
            using (var запись = архив.CreateEntry("x.example.com.pem").Open())
            {
                запись.Write(pem);
            }

            using (var ключ = архив.CreateEntry("x.example.com.key").Open())
            {
                ключ.Write("-----BEGIN CERTIFICATE-----\nне-сертификат\n-----END CERTIFICATE-----"u8);
            }
        }

        CertificateFiles.Ders(буфер.ToArray()).Count.ShouldBe(2, "ключ из архива не читается вовсе");
        CertificateFiles.Ders("мусор"u8.ToArray()).ShouldBeEmpty();
        CertificateFiles.Ders([]).ShouldBeEmpty();
    }

    private async Task<T> Источник<T>(Func<ICertificateSource, Task<T>> действие)
    {
        await using var scope = база.Scope();

        return await действие(scope.ServiceProvider.GetRequiredService<ICertificateSource>());
    }

    /// <summary>Лист, подписанный промежуточным.</summary>
    internal static (X509Certificate2 Лист, X509Certificate2 Промежуточный) Цепочка(string имя)
    {
        using var ключЦентра = ECDsa.Create();
        var запросЦентра = new CertificateRequest("CN=Test Intermediate", ключЦентра, HashAlgorithmName.SHA256);
        запросЦентра.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        var центр = запросЦентра.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));

        using var ключ = ECDsa.Create();
        var запрос = new CertificateRequest($"CN={имя}", ключ, HashAlgorithmName.SHA256);
        var лист = запрос.Create(центр, DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(90), [1, 2, 3]);

        return (лист, центр);
    }

    private static byte[] P7b(params X509Certificate2[] сертификаты)
    {
        return new X509Certificate2Collection(сертификаты).Export(X509ContentType.Pkcs7)!;
    }
}
