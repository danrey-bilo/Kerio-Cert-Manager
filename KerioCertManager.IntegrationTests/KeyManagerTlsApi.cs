using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json.Nodes;
using KerioCertManager.Modules.KeyManager;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace KerioCertManager.IntegrationTests;

/// <summary>Настоящий соединитель против HTTPS-заглушки Key Manager Plus с самоподписанным сертификатом.</summary>
[Collection(KerioCertManagerHost.Collection)]
public sealed class KeyManagerTlsApi(KerioCertManagerHost host) : IAsyncLifetime
{
    private const string Ключ = "75408B31-C7D6-4F5A-9A0E-1B2C3D4E5F60";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly ЗаглушкаKmp _kmp = new(Ключ);

    public async ValueTask InitializeAsync()
    {
        await _kmp.ЗапуститьAsync();

        host.User.SignIn();
        host.Settings.Values.Clear();
        host.Settings.Values["keymanager.base_url"] = _kmp.Адрес;

        (await host.CreateClient().PutAsJsonAsync("/api/keymanager/api-key", new { api_key = Ключ }, Ct))
            .StatusCode.ShouldBe(HttpStatusCode.NoContent);
    }

    public async ValueTask DisposeAsync()
    {
        host.User.SignIn();
        host.Settings.Values.Clear();

        var клиент = host.CreateClient();
        await клиент.PutAsJsonAsync("/api/keymanager/api-key", new { api_key = "" }, CancellationToken.None);

        host.User.SignOut();
        Корни(host.Services).Clear();
        await _kmp.DisposeAsync();
    }

    [Fact]
    public async Task Самоподписанный_сервер_отвергается()
    {
        var ответ = await host.CreateClient().PostAsync("/api/keymanager/test", null, Ct);

        ответ.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        var тело = JsonNode.Parse(await ответ.Content.ReadAsStringAsync(Ct))!;
        тело["code"]!.GetValue<string>().ShouldBe("KEYMANAGER_UNAVAILABLE");
        тело["details"]!["reason"]!.GetValue<string>().ShouldContain("самоподписанный не принимается");

        _kmp.Запросов.ShouldBe(0, "до сервера с непроверенным сертификатом ключ не доехал");
    }

    [Fact]
    public async Task Сертификат_доверенного_корня_пропускает_и_ключ_едет_заголовком()
    {
        Довериться(_kmp);

        var ответ = await host.CreateClient().PostAsync("/api/keymanager/test", null, Ct);

        ответ.StatusCode.ShouldBe(HttpStatusCode.OK);
        JsonNode.Parse(await ответ.Content.ReadAsStringAsync(Ct))!["certificates"]!.GetValue<int>().ShouldBe(1);
        _kmp.Запросов.ShouldBe(1);
    }

    [Fact]
    public async Task Ключ_не_уходит_на_подменённый_адрес()
    {
        // ⚠ Сертификат подменного сервера проходит проверку — отказывает привязка ключа к адресу.
        await using var подменный = new ЗаглушкаKmp("ключ-подменного");
        await подменный.ЗапуститьAsync();
        Довериться(подменный);

        host.Settings.Values["keymanager.base_url"] = подменный.Адрес;

        var ответ = await host.CreateClient().PostAsync("/api/keymanager/test", null, Ct);

        ответ.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        var причина = JsonNode.Parse(await ответ.Content.ReadAsStringAsync(Ct))!["details"]!["reason"]!.GetValue<string>();
        причина.ShouldContain("заново");
        причина.ShouldNotContain(Ключ);

        подменный.Запросов.ShouldBe(0, "до подменного сервера не дошло ни одного запроса — и ключа");
    }

    /// <summary>Корни, которым соединение с Key Manager Plus доверяет в этом хосте.</summary>
    private static X509Certificate2Collection Корни(IServiceProvider services) =>
        services.GetRequiredService<KeyManagerModuleOptions>().TrustedRoots;

    /// <summary>Доверять заглушке, как рабочий хост — удостоверяющему центру.</summary>
    private void Довериться(ЗаглушкаKmp заглушка) => Корни(host.Services).Add(заглушка.Корень);

    /// <summary>Key Manager Plus в процессе: HTTPS с самоподписанным сертификатом и списком сертификатов.</summary>
    private sealed class ЗаглушкаKmp(string ключ) : IAsyncDisposable
    {
        private WebApplication? _приложение;
        private X509Certificate2? _сертификат;
        private int _запросов;

        public string Адрес { get; private set; } = "";

        /// <summary>Сертификат заглушки — он же корень своей цепочки.</summary>
        public X509Certificate2 Корень => _сертификат ?? throw new InvalidOperationException("заглушка не запущена");

        public int Запросов => _запросов;

        public async Task ЗапуститьAsync()
        {
            using var пара = RSA.Create(2048);
            var запрос = new CertificateRequest("CN=kmp.example.test", пара, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);

            // ⚠ Имя из адреса — в SAN: проверку имени хоста не выключить.
            var имена = new SubjectAlternativeNameBuilder();
            имена.AddIpAddress(IPAddress.Loopback);
            имена.AddDnsName("localhost");
            запрос.CertificateExtensions.Add(имена.Build());

            using var временный = запрос.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(7));

            // ⚠ Через PKCS#12: SChannel не принимает эфемерный ключ, а CreateSelfSigned даёт именно его.
            _сертификат = X509CertificateLoader.LoadPkcs12(временный.Export(X509ContentType.Pkcs12), null);

            var строитель = WebApplication.CreateSlimBuilder();
            строитель.WebHost.ConfigureKestrel(kestrel =>
                kestrel.Listen(IPAddress.Loopback, 0, порт => порт.UseHttps(_сертификат)));

            _приложение = строитель.Build();

            _приложение.MapGet("/api/pki/restapi/{метод}", (string метод, HttpContext контекст) =>
            {
                Interlocked.Increment(ref _запросов);

                if (контекст.Request.Headers["AUTHTOKEN"] != ключ || контекст.Request.QueryString.Value!.Contains(ключ))
                {
                    return Results.Json(new { result = new { status = "Failed", message = "Invalid API key" } });
                }

                if (метод == "getAllSSLCertsExpiryDate" && контекст.Request.Query.Count > 0)
                {
                    return Results.Text("EXTRA_PARAM_FOUND", "text/html", statusCode: 400);
                }

                return метод == "getAllSSLCertsExpiryDate"
                    ? Results.Json(new
                    {
                        name = "GetAllSSLCertificatesExpiryDate",
                        result = new { status = "Success", message = "Certificates expiry date fetched successfully" },
                        details = new[]
                        {
                            new Dictionary<string, string>
                            {
                                ["Common Name"] = "site.example.com",
                                ["serialNumber"] = "0a1b2c",
                                ["ExpiryDate"] = "Oct 2, 2027",
                            },
                        },
                    })
                    : Results.NotFound();
            });

            await _приложение.StartAsync();

            Адрес = _приложение.Services.GetRequiredService<IServer>()
                .Features.Get<IServerAddressesFeature>()!.Addresses.Single();
        }

        public async ValueTask DisposeAsync()
        {
            if (_приложение is not null)
            {
                await _приложение.DisposeAsync();
            }

            _сертификат?.Dispose();
        }
    }
}
