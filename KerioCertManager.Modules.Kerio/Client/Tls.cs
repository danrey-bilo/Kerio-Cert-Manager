using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using KerioCertManager.Modules.Kerio.Certificates;
using KerioCertManager.Modules.Kerio.Domain;

namespace KerioCertManager.Modules.Kerio.Client;

/// <summary>Сертификат, который сервер подал в TLS-рукопожатии.</summary>
internal sealed record ServedCertificate(
    string Sha256,
    string Subject,
    string Issuer,
    string Serial,
    DateTimeOffset NotBefore,
    DateTimeOffset NotAfter,
    IReadOnlyList<string> DnsNames,
    bool SelfSigned,
    bool ChainTrusted,
    string Pem);

/// <summary>Сбой TLS: соединение, рукопожатие или недоверенный сертификат.</summary>
internal sealed class TlsException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>Что подаёт сервер по TLS — без доверия: только наблюдение.</summary>
internal interface ITlsInspector
{
    Task<ServedCertificate> InspectAsync(string host, int port, CancellationToken cancellationToken = default);

    /// <summary>То же, но с выбранным именем SNI: Kerio с 9.0.2 подаёт сертификат по имени, которое назвал клиент.</summary>
    Task<ServedCertificate> InspectAsync(string host, int port, string? serverName, CancellationToken cancellationToken = default) =>
        InspectAsync(host, port, cancellationToken);
}

/// <summary>Имена для SNI.</summary>
internal static class Sni
{
    /// <summary>Без SNI — сертификат по умолчанию.</summary>
    public const string Default = "";

    /// <summary>Имя, которым спросить сертификат с этим Common Name; подстановка — придуманным первым уровнем.</summary>
    public static string? For(string? commonName)
    {
        var имя = (commonName ?? string.Empty).Trim().TrimEnd('.').ToLowerInvariant();

        if (имя.StartsWith("*.", StringComparison.Ordinal))
        {
            имя = "kcm-check" + имя[1..];
        }

        return имя.Length is > 0 and <= 253
            && !System.Net.IPAddress.TryParse(имя, out _)
            && Uri.CheckHostName(имя) == UriHostNameType.Dns
            && имя.Contains('.', StringComparison.Ordinal)
                ? имя
                : null;
    }
}

internal sealed class TlsInspector : ITlsInspector
{
    private static readonly TimeSpan Таймаут = TimeSpan.FromSeconds(10);

    public Task<ServedCertificate> InspectAsync(string host, int port, CancellationToken cancellationToken = default) =>
        InspectAsync(host, port, null, cancellationToken);

    public async Task<ServedCertificate> InspectAsync(string host, int port, string? serverName, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(host);

        using var срок = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        срок.CancelAfter(Таймаут);

        X509Certificate2? лист = null;
        var доверен = false;

        try
        {
            using var tcp = new TcpClient();
            await tcp.ConnectAsync(host, port, срок.Token);

            await using var tls = new SslStream(tcp.GetStream(), leaveInnerStreamOpen: false);

            await tls.AuthenticateAsClientAsync(
                new SslClientAuthenticationOptions
                {
                    TargetHost = serverName ?? host,
                    CertificateRevocationCheckMode = X509RevocationMode.NoCheck,

                    // ⚠ Без возобновления: возобновлённая сессия не несёт сертификата и показала бы прежний.
                    AllowTlsResume = false,
                    RemoteCertificateValidationCallback = (_, сертификат, цепь, ошибки) =>
                    {
                        if (сертификат is not null)
                        {
                            лист = X509CertificateLoader.LoadCertificate(сертификат.GetRawCertData());
                        }

                        доверен = (ошибки & ~SslPolicyErrors.RemoteCertificateNameMismatch) == SslPolicyErrors.None;

                        // ⚠ Только смотрим: соединение закрывается, данных по нему не идёт.
                        return true;
                    },
                },
                срок.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TlsException($"{host}:{port} не ответил за {Таймаут.TotalSeconds:0} с.");
        }
        catch (SocketException ошибка)
        {
            throw new TlsException($"{host}:{port} недоступен: {Сеть(ошибка)}.", ошибка);
        }
        catch (Exception ошибка) when (ошибка is IOException or System.Security.Authentication.AuthenticationException)
        {
            throw new TlsException($"{host}:{port}: TLS-рукопожатие не удалось.", ошибка);
        }

        if (лист is null)
        {
            throw new TlsException($"{host}:{port} не подал сертификат.");
        }

        using (лист)
        {
            return Describe(лист, доверен);
        }
    }

    internal static ServedCertificate Describe(X509Certificate2 лист, bool доверен) => new(
        Fingerprints.Sha256(лист),
        Fingerprints.CommonName(лист.SubjectName),
        Fingerprints.CommonName(лист.IssuerName),
        Fingerprints.Serial(лист.SerialNumber),
        new DateTimeOffset(лист.NotBefore.ToUniversalTime(), TimeSpan.Zero),
        new DateTimeOffset(лист.NotAfter.ToUniversalTime(), TimeSpan.Zero),
        Fingerprints.DnsNames(лист),
        лист.SubjectName.RawData.AsSpan().SequenceEqual(лист.IssuerName.RawData),
        доверен,
        лист.ExportCertificatePem());

    private static string Сеть(SocketException ошибка) => ошибка.SocketErrorCode switch
    {
        SocketError.ConnectionRefused => "порт закрыт",
        SocketError.HostNotFound or SocketError.NoData => "имя не разрешается",
        SocketError.TimedOut => "нет ответа",
        SocketError.HostUnreachable or SocketError.NetworkUnreachable => "нет маршрута",
        _ => ошибка.SocketErrorCode.ToString(),
    };
}

/// <summary>Доверие к сертификату порта администрирования Kerio — для сессии API с паролем.</summary>
internal static class TlsTrust
{
    public static RemoteCertificateValidationCallback For(KerioServer server, IReadOnlyCollection<string>? alsoTrusted = null)
    {
        ArgumentNullException.ThrowIfNull(server);

        var режим = server.TlsMode;
        var пин = server.TlsPinSha256;
        var имя = string.IsNullOrWhiteSpace(server.TlsServerName) ? null : server.TlsServerName.Trim();
        var свои = alsoTrusted?.ToHashSet(StringComparer.Ordinal) ?? [];

        return (_, сертификат, цепь, ошибки) =>
            Accept(режим, пин, имя, сертификат, цепь, ошибки)
            || (сертификат is not null
                && свои.Count > 0
                && свои.Contains(Fingerprints.Sha256(сертификат.GetRawCertData()))
                && СвойНаИмя(режим, имя, сертификат, ошибки));
    }

    /// <summary>Свой сертификат минует цепочку, но в режиме УЦ не имя: ключ из KMP мог уйти и на другой узел.</summary>
    internal static bool СвойНаИмя(string режим, string? имя, X509Certificate сертификат, SslPolicyErrors ошибки)
    {
        if (режим != TlsModes.Ca)
        {
            return true;
        }

        if (имя is null)
        {
            return (ошибки & SslPolicyErrors.RemoteCertificateNameMismatch) == 0;
        }

        using var лист = X509CertificateLoader.LoadCertificate(сертификат.GetRawCertData());
        return лист.MatchesHostname(имя, allowWildcards: true, allowCommonName: true);
    }

    internal static bool Accept(
        string режим, string? пин, string? имя, X509Certificate? сертификат, X509Chain? цепь, SslPolicyErrors ошибки)
    {
        if (сертификат is null)
        {
            return false;
        }

        using var лист = X509CertificateLoader.LoadCertificate(сертификат.GetRawCertData());

        if (режим == TlsModes.Pin)
        {
            return пин is not null && string.Equals(Fingerprints.Sha256(лист), пин, StringComparison.Ordinal);
        }

        if (режим != TlsModes.Ca)
        {
            return false;
        }

        if ((ошибки & SslPolicyErrors.RemoteCertificateNotAvailable) != 0)
        {
            return false;
        }

        if ((ошибки & SslPolicyErrors.RemoteCertificateChainErrors) != 0 && !ТолькоОтзыв(цепь))
        {
            return false;
        }

        if ((ошибки & SslPolicyErrors.RemoteCertificateNameMismatch) != 0)
        {
            return имя is not null && лист.MatchesHostname(имя, allowWildcards: true, allowCommonName: true);
        }

        return имя is null || лист.MatchesHostname(имя, allowWildcards: true, allowCommonName: true);
    }

    /// <summary>Ошибки цепочки — только «статус отзыва неизвестен»: в закрытой сети списки отзыва недоступны.</summary>
    private static bool ТолькоОтзыв(X509Chain? цепь) =>
        цепь is not null
        && цепь.ChainStatus.Length > 0
        && цепь.ChainStatus.All(с => с.Status is X509ChainStatusFlags.NoError
            or X509ChainStatusFlags.RevocationStatusUnknown
            or X509ChainStatusFlags.OfflineRevocation);
}
