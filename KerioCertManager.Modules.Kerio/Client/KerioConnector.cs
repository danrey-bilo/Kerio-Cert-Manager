using System.Net;
using System.Reflection;
using KerioCertManager.Modules.Kerio.Certificates;
using KerioCertManager.Modules.Kerio.Domain;
using KerioCertManager.Modules.Kerio.Secrets;

namespace KerioCertManager.Modules.Kerio.Client;

/// <summary>Открывает сессию Kerio с политикой доверия сервера; интерфейс — ради тестов без HTTPS.</summary>
internal interface IKerioConnector
{
    /// <summary>Войти паролем, который ещё не сохранён (проверка перед сохранением).</summary>
    Task<IKerioSession> OpenAsync(KerioServer server, string password, CancellationToken cancellationToken = default);

    /// <summary>Войти сохранённым паролем сервера.</summary>
    Task<IKerioSession> OpenAsync(KerioServer server, CancellationToken cancellationToken = default);

    /// <summary>Войти сохранённым паролем, доверяя сверх режима сертификатам <paramref name="alsoTrustedSha256"/>.</summary>
    Task<IKerioSession> OpenAsync(
        KerioServer server, IReadOnlyCollection<string> alsoTrustedSha256, CancellationToken cancellationToken = default);
}

/// <summary>Свежий обработчик на каждую сессию: адрес, логин и доверие правятся без перезапуска.</summary>
internal sealed class KerioConnector(ServerPasswords passwords) : IKerioConnector
{
    private static readonly TimeSpan Таймаут = TimeSpan.FromSeconds(60);

    private const int ПотолокОтвета = 8 * 1024 * 1024;

    private static readonly string Версия =
        typeof(KerioConnector).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            .Split('+')[0] ?? "1.0";

    public Task<IKerioSession> OpenAsync(KerioServer server, CancellationToken cancellationToken = default) =>
        OpenAsync(server, [], cancellationToken);

    public Task<IKerioSession> OpenAsync(
        KerioServer server, IReadOnlyCollection<string> alsoTrustedSha256, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(server);

        string пароль;

        try
        {
            пароль = passwords.Open(server);
        }
        catch (InvalidOperationException ошибка)
        {
            throw new KerioException(KerioFailure.Authentication, ошибка.Message, inner: ошибка);
        }

        var имя = server.TlsServerName ?? BaseUri(server.BaseUrl).Host;
        IReadOnlyCollection<string> свои =
        [
            .. alsoTrustedSha256,
            .. new[] { server.InstalledSha256 }
                .Concat(server.Extras.Where(д => Fingerprints.NameCovers(д.CommonName, имя)).Select(д => д.InstalledSha256))
                .OfType<string>(),
        ];

        return ОткрытьAsync(server, пароль, свои, cancellationToken);
    }

    public Task<IKerioSession> OpenAsync(KerioServer server, string password, CancellationToken cancellationToken = default) =>
        ОткрытьAsync(server, password, [], cancellationToken);

    private static async Task<IKerioSession> ОткрытьAsync(
        KerioServer server, string password, IReadOnlyCollection<string> свои, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(server);

        var адрес = BaseUri(server.BaseUrl);

        var обработчик = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseCookies = true,
            CookieContainer = new CookieContainer(),
            ConnectTimeout = TimeSpan.FromSeconds(15),
            PooledConnectionLifetime = TimeSpan.FromMinutes(2),
            AutomaticDecompression = DecompressionMethods.None,
        };

        обработчик.SslOptions.RemoteCertificateValidationCallback = TlsTrust.For(server, свои);

        var http = new HttpClient(обработчик, disposeHandler: true)
        {
            BaseAddress = адрес,
            Timeout = Таймаут,
            MaxResponseContentBufferSize = ПотолокОтвета,
        };

        return await KerioSession.LoginAsync(http, server.Username, password, Версия, cancellationToken);
    }

    /// <summary>Адрес администрирования: только https, без пути.</summary>
    internal static Uri BaseUri(string baseUrl)
    {
        if (!Uri.TryCreate(baseUrl?.Trim(), UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
        {
            throw new KerioException(
                KerioFailure.Transport, "Адрес Kerio должен быть https://… : пароль администратора по открытому HTTP прочтёт любой на пути.");
        }

        return new Uri($"https://{uri.Authority}/");
    }
}
