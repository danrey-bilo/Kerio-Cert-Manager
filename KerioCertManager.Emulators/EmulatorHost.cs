using System.Net;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Microsoft.AspNetCore.Server.Kestrel.Https;

namespace KerioCertManager.Emulators;

/// <summary>Один сервер Kerio стенда: имя, порт администрирования, порты служб.</summary>
public sealed record KerioSpec(string Name, int AdminPort, int[] ServicePorts);

/// <summary>Параметры стенда.</summary>
public sealed record EmulatorOptions
{
    /// <summary>Порт панели стенда (HTTP).</summary>
    public int ControlPort { get; init; } = 8080;

    public int KmpPort { get; init; } = 6565;

    public string KmpApiKey { get; init; } = "demo-api-key";

    /// <summary>Имена в сертификате HTTPS эмулятора KMP.</summary>
    public string[] KmpHostNames { get; init; } = ["localhost", "emulators", "kmp.demo.local"];

    public IReadOnlyList<KerioSpec> Kerio { get; init; } = [new("kerio-a", 4041, [9931]), new("kerio-b", 4042, [9932])];

    public string KerioUser { get; init; } = "kcm";

    public string KerioPassword { get; init; } = "Demo-Passw0rd!";

    /// <summary>Сертификаты в KMP и на серверах в начале: имя → сколько дней осталось у установленного.</summary>
    public IReadOnlyList<(string CommonName, int DaysLeft)> Certificates { get; init; } =
        [("mail.demo.local", 12), ("*.demo.local", 200)];

    /// <summary>Каталог для УЦ и пакета доверия; <c>null</c> — в памяти.</summary>
    public string? DataDirectory { get; init; }

    /// <summary>Засеять серверы и KMP начальными сертификатами.</summary>
    public bool Seed { get; init; } = true;

    /// <summary>Положить в хранилище каждого Kerio ещё и сертификаты не из KMP: скоро истекающий и истёкший — для обзора демо.</summary>
    public bool SeedForeign { get; init; }

    /// <summary>Через сколько секунд после изменения хранилища истекают сессии API, как у настоящего Kerio (~25); 0 — нет.</summary>
    public int SessionResetSeconds { get; init; }

    /// <summary>Адрес панели стенда, как его видит сервис: из него — точки списков отзыва в сертификатах.</summary>
    public string? PublicControlUrl { get; init; }
}

/// <summary>Стенд: эмулятор Key Manager Plus и серверов Kerio Connect по HTTPS, панель управления по HTTP.</summary>
public sealed class EmulatorHost : IAsyncDisposable
{
    private EmulatorHost(WebApplication app, TestPki pki, KmpEmulator kmp, IReadOnlyDictionary<int, KerioEmulator> kerio)
    {
        App = app;
        Pki = pki;
        Kmp = kmp;
        KerioByPort = kerio;
    }

    public WebApplication App { get; }

    public TestPki Pki { get; }

    public KmpEmulator Kmp { get; }

    /// <summary>Сервер Kerio по любому его порту.</summary>
    public IReadOnlyDictionary<int, KerioEmulator> KerioByPort { get; }

    public IReadOnlyList<KerioEmulator> Kerio => [.. KerioByPort.Values.Distinct()];

    public KerioEmulator KerioNamed(string name) => Kerio.First(к => к.Name == name);

    public static EmulatorHost Build(EmulatorOptions options, string[]? args = null)
    {
        ArgumentNullException.ThrowIfNull(options);

        var pki = TestPki.LoadOrCreate(options.DataDirectory, options.PublicControlUrl ?? $"http://localhost:{options.ControlPort}");
        var kmp = new KmpEmulator(pki, options.KmpApiKey);
        var kmpTls = pki.Issue(options.KmpHostNames, DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(2));
        var kmpСерт = TestPki.ForServer(kmpTls);

        var серверы = new Dictionary<int, KerioEmulator>();

        foreach (var spec in options.Kerio)
        {
            var kerio = new KerioEmulator(spec.Name, spec.AdminPort, spec.ServicePorts, options.KerioUser, options.KerioPassword)
            {
                SessionResetAfterChange = options.SessionResetSeconds > 0 ? TimeSpan.FromSeconds(options.SessionResetSeconds) : null,
            };
            серверы[spec.AdminPort] = kerio;

            foreach (var порт in spec.ServicePorts)
            {
                серверы[порт] = kerio;
            }
        }

        if (options.Seed)
        {
            var цепьПром = pki.Intermediate.ExportCertificatePem() + "\n" + pki.Root.ExportCertificatePem();

            foreach (var (имя, дней) in options.Certificates)
            {
                var срок = Math.Max(90, дней + 30);
                var выпуск = kmp.Issue(имя, TimeSpan.FromDays(срок), TimeSpan.FromDays(срок - дней));

                foreach (var kerio in серверы.Values.Distinct().Where((_, i) => i < options.Certificates.Count))
                {
                    if (kerio.Store.Count == 1 && ЧейСертификат(kerio, options) == имя)
                    {
                        kerio.Seed(выпуск.Certificate, выпуск.Certificate.ExportCertificatePem() + "\n" + цепьПром);
                    }
                }
            }

            if (options.SeedForeign)
            {
                foreach (var kerio in серверы.Values.Distinct())
                {
                    var сейчас = DateTimeOffset.UtcNow;
                    var скоро = pki.Issue([$"autodiscover.{kerio.Name}.demo.local"], сейчас.AddDays(-356), сейчас.AddDays(9));
                    var истёк = pki.Issue([$"legacy.{kerio.Name}.demo.local"], сейчас.AddDays(-400), сейчас.AddDays(-35));

                    kerio.Keep(скоро, скоро.ExportCertificatePem() + "\n" + цепьПром);
                    kerio.Keep(истёк, истёк.ExportCertificatePem() + "\n" + цепьПром);
                }
            }
        }

        var builder = WebApplication.CreateSlimBuilder(args ?? []);
        builder.Logging.SetMinimumLevel(LogLevel.Warning);

        builder.WebHost.ConfigureKestrel(kestrel =>
        {
            kestrel.ListenAnyIP(options.ControlPort);

            kestrel.ListenAnyIP(options.KmpPort, listen => listen.UseHttps(https =>
            {
                https.ServerCertificate = kmpСерт;

                https.ServerCertificateChain = [pki.Intermediate];
                https.SslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13;
            }));

            foreach (var (порт, kerio) in серверы)
            {
                kestrel.ListenAnyIP(порт, listen => listen.UseHttps(new TlsHandshakeCallbackOptions
                {
                    OnConnection = context =>
                    {
                        // ⚠ Каждое рукопожатие — с тем, что подаётся сейчас для этого имени, и с цепочкой; без возобновления сессий.
                        var (сертификат, встроенный) = kerio.CertificateFor(context.ClientHelloInfo.ServerName);

                        return ValueTask.FromResult(new System.Net.Security.SslServerAuthenticationOptions
                        {
                            ServerCertificateContext = System.Net.Security.SslStreamCertificateContext.Create(
                                сертификат, встроенный ? [] : [pki.Intermediate], offline: true),
                            EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                            AllowTlsResume = false,
                        });
                    },
                }));
            }
        });

        var app = builder.Build();

        app.MapGet("/api/pki/restapi/{method}", (HttpContext context, string method) =>
            context.Connection.LocalPort == options.KmpPort ? kmp.Handle(context, method) : Results.NotFound());

        app.MapPost("/admin/api/jsonrpc/", (Delegate)((HttpContext context) =>
            серверы.TryGetValue(context.Connection.LocalPort, out var kerio) && kerio.AdminPort == context.Connection.LocalPort
                ? kerio.RpcAsync(context)
                : Task.FromResult(Results.NotFound())));

        app.MapPost("/admin/api/jsonrpc/upload/", (Delegate)((HttpContext context) =>
            серверы.TryGetValue(context.Connection.LocalPort, out var kerio) && kerio.AdminPort == context.Connection.LocalPort
                ? kerio.UploadAsync(context)
                : Task.FromResult(Results.NotFound())));

        ControlPanel.Map(app, options, pki, kmp, серверы);

        if (options.DataDirectory is not null)
        {
            File.WriteAllText(Path.Combine(options.DataDirectory, "demo-ca.pem"), pki.Root.ExportCertificatePem() + "\n");
            File.WriteAllText(Path.Combine(options.DataDirectory, "trust-bundle.pem"), ПакетДоверия(pki));
        }

        return new EmulatorHost(app, pki, kmp, серверы);
    }

    /// <summary>Системные корни и корень стенда одним файлом — для <c>SSL_CERT_FILE</c> сервиса в демо-стеке.</summary>
    private static string ПакетДоверия(TestPki pki)
    {
        var пакет = new StringBuilder();

        foreach (var системный in new[] { "/etc/ssl/certs/ca-certificates.crt", "/etc/pki/tls/certs/ca-bundle.crt" })
        {
            if (File.Exists(системный))
            {
                пакет.Append(File.ReadAllText(системный)).Append('\n');
                break;
            }
        }

        пакет.Append(pki.Root.ExportCertificatePem()).Append('\n');

        return пакет.ToString();
    }

    /// <summary>Какой сертификат стоит на сервере в начале: первый сервер — первое имя, второй — второе.</summary>
    private static string ЧейСертификат(KerioEmulator kerio, EmulatorOptions options)
    {
        var номер = options.Kerio.ToList().FindIndex(s => s.Name == kerio.Name);

        return options.Certificates[Math.Min(номер, options.Certificates.Count - 1)].CommonName;
    }

    public Task StartAsync(CancellationToken cancellationToken = default) => App.StartAsync(cancellationToken);

    public async ValueTask DisposeAsync()
    {
        await App.StopAsync();
        await App.DisposeAsync();
    }

    /// <summary>Свободный порт для тестов.</summary>
    public static int FreePort()
    {
        var слушатель = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        слушатель.Start();
        var порт = ((IPEndPoint)слушатель.LocalEndpoint).Port;
        слушатель.Stop();

        return порт;
    }

    /// <summary>Пароль ключа выпуска — для отладки стенда.</summary>
    public static string Describe(X509Certificate2 c) =>
        $"{c.GetNameInfo(X509NameType.SimpleName, false)} #{c.SerialNumber} до {c.NotAfter:yyyy-MM-dd}";
}
