using KerioCertManager.Modules.Kerio.Certificates;
using KerioCertManager.Modules.Kerio.Client;
using KerioCertManager.Modules.Kerio.Domain;
using Modules.Core.Errors;

namespace KerioCertManager.Modules.Kerio.Sync;

/// <summary>Итог проверки сервера.</summary>
internal sealed record InspectionResult(
    ServedCertificate? Served,
    string? TlsError,
    bool ApiChecked,
    string? ApiError,
    KerioFailure? ApiFailure,
    string? Version,
    string? Role,
    IReadOnlyList<KerioCertificate>? Certificates,
    IReadOnlyList<StoredCertificate>? Store = null,
    IReadOnlyDictionary<string, string>? ServedByName = null)
{
    /// <summary>Главная причина неудачи; <c>null</c> — всё в порядке.</summary>
    public string? Error => TlsError ?? ApiError;
}

/// <summary>Проверка сервера: что подаётся по TLS и пускает ли API.</summary>
internal sealed class ServerInspection(IKerioConnector connector, ITlsInspector tls)
{
    /// <summary>Роль Kerio, у которой есть право на сертификаты.</summary>
    internal const string FullAdmin = "FullAdmin";

    public async Task<InspectionResult> InspectAsync(KerioServer server, bool forceApi, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(server);

        var итог = await ПроверитьAsync(server, forceApi, cancellationToken);

        if (итог.Served is not { } подан)
        {
            return итог;
        }

        var адрес = KerioConnector.BaseUri(server.BaseUrl);

        return итог with { ServedByName = await ПоИменамAsync(server, адрес, cancellationToken) };
    }

    /// <summary>Что Kerio подаёт для каждого ведомого имени — рукопожатием с этим именем в SNI.</summary>
    private async Task<IReadOnlyDictionary<string, string>> ПоИменамAsync(KerioServer server, Uri адрес, CancellationToken cancellationToken)
    {
        var итог = new System.Collections.Concurrent.ConcurrentDictionary<string, string>(StringComparer.Ordinal);
        var имена = server.ManagedNames().Select(Sni.For).OfType<string>().Distinct(StringComparer.Ordinal);

        await Parallel.ForEachAsync(
            имена,
            new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = cancellationToken },
            async (имя, token) =>
            {
                try
                {
                    итог[имя] = (await tls.InspectAsync(адрес.Host, адрес.Port, имя, token)).Sha256;
                }
                catch (TlsException)
                {
                }
            });

        return итог.ToDictionary(StringComparer.Ordinal);
    }

    private async Task<InspectionResult> ПроверитьAsync(KerioServer server, bool forceApi, CancellationToken cancellationToken)
    {
        Uri адрес;

        try
        {
            адрес = KerioConnector.BaseUri(server.BaseUrl);
        }
        catch (KerioException ошибка)
        {
            return new InspectionResult(null, ошибка.Message, false, null, null, null, null, null);
        }

        ServedCertificate? подан = null;
        string? ошибкаTls = null;

        try
        {
            подан = await tls.InspectAsync(адрес.Host, адрес.Port, Sni.Default, cancellationToken);
        }
        catch (TlsException ошибка)
        {
            ошибкаTls = ошибка.Message;
        }

        if (подан is null)
        {
            return new InspectionResult(null, ошибкаTls, false, null, null, null, null, null);
        }

        if ((server.AuthRejected && !forceApi) || server.Password is null)
        {
            return new InspectionResult(
                подан,
                null,
                false,
                server.Password is null
                    ? "Пароль учётной записи Kerio не задан."
                    : "Kerio отверг логин или пароль — сервис не входит, пока их не сменят или не нажмут «Проверить».",
                KerioFailure.Authentication,
                null,
                null,
                null);
        }

        try
        {
            await using var сессия = await connector.OpenAsync(server, cancellationToken);

            var кто = await сессия.WhoAmIAsync(cancellationToken);
            var версия = await сессия.VersionAsync(cancellationToken);
            var сертификаты = await сессия.CertificatesAsync(cancellationToken);
            var хранилище = await ХранилищеAsync(сессия, сертификаты, cancellationToken, server.Store);

            var отказ = кто.Role == FullAdmin
                ? null
                : $"Роль учётной записи в Kerio — {(string.IsNullOrEmpty(кто.Role) ? "неизвестна" : кто.Role)}: для установки сертификатов нужна "
                  + "роль полного администратора (Full administration).";

            return new InspectionResult(
                подан, null, true, отказ, отказ is null ? null : KerioFailure.Forbidden, версия, кто.Role, сертификаты, хранилище);
        }
        catch (KerioException ошибка)
        {
            return new InspectionResult(подан, null, true, ошибка.Message, ошибка.Kind, null, null, null);
        }
        catch (Exception ошибка) when (ошибка is not OperationCanceledException)
        {
            return new InspectionResult(
                подан, null, true, $"Проверка Kerio не удалась: {SafeError.Describe(ошибка)}.", KerioFailure.Transport, null, null, null);
        }
    }

    /// <summary>Хранилище с отпечатком и сроком каждого серверного сертификата — только чтением (<c>toSource</c>).</summary>
    internal static async Task<IReadOnlyList<StoredCertificate>> ХранилищеAsync(
        IKerioSession session,
        IReadOnlyList<KerioCertificate> certificates,
        CancellationToken cancellationToken,
        IReadOnlyList<StoredCertificate>? прежние = null)
    {
        var итог = new List<StoredCertificate>();

        foreach (var с in certificates)
        {
            DateTimeOffset? доПоСписку = с.ValidTo is { } дата ? new DateTimeOffset(дата.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero) : null;

            var запись = new StoredCertificate(
                с.Id, с.Type, с.Subject, с.Issuer, null, доПоСписку, null, null, [], с.IsSelfSigned, с.IsUntrusted);

            if (с.IsServerCertificate && !string.IsNullOrEmpty(с.Id))
            {
                try
                {
                    using var текст = Fingerprints.FirstFromPem(await session.SourceAsync(с.Id, cancellationToken));

                    if (текст is not null)
                    {
                        запись = запись with
                        {
                            NotBefore = текст.NotBefore.ToUniversalTime(),
                            NotAfter = текст.NotAfter.ToUniversalTime(),
                            Serial = Fingerprints.Serial(текст.SerialNumber),
                            Sha256 = Fingerprints.Sha256(текст),
                            DnsNames = Fingerprints.DnsNames(текст),
                        };
                    }
                }
                catch (KerioException ошибка) when (ошибка.Kind is not (KerioFailure.Authentication or KerioFailure.Tls))
                {
                    // ⚠ Текст не прочитан: прежние данные той же записи лучше пустых — иначе стоящий выпуск покажется недостающим.
                    if (прежние?.FirstOrDefault(п => п.Id == с.Id && п.Subject == с.Subject && п.Serial is not null) is { } был)
                    {
                        запись = был with { Type = с.Type, Untrusted = с.IsUntrusted };
                    }
                }
            }

            итог.Add(запись);
        }

        return итог;
    }

    /// <summary>
    /// Записать итог проверки в строку сервера. Счётчики сбоев — отдельно для TLS и API. <paramref name="считатьСбои"/> —
    /// нет в проходах очереди: они идут каждые 45 секунд, в том числе сквозь перезапуск HTTPS после установки.
    /// </summary>
    public static void Apply(KerioServer server, InspectionResult result, DateTimeOffset now, bool считатьСбои = true)
    {
        ArgumentNullException.ThrowIfNull(server);
        ArgumentNullException.ThrowIfNull(result);

        server.LastCheckAt = now;
        server.LastCheckError = result.Error;

        if (result.Served is not null)
        {
            server.TlsFailures = 0;
        }
        else if (считатьСбои)
        {
            server.TlsFailures++;
        }

        if (result.ApiError is null && result.ApiChecked)
        {
            server.CheckFailures = 0;
        }
        else if (считатьСбои && result.ApiError is not null)
        {
            server.CheckFailures++;
        }

        if (result.Served is { } подан)
        {
            server.ServedSha256 = подан.Sha256;
            server.ServedSubject = подан.Subject;
            server.ServedIssuer = подан.Issuer;
            server.ServedSerial = подан.Serial;
            server.ServedNotBefore = подан.NotBefore;
            server.ServedNotAfter = подан.NotAfter;
        }

        if (result.ApiChecked)
        {
            server.AuthRejected = result.ApiFailure == KerioFailure.Authentication;

            if (result.Role is not null)
            {
                server.KerioRole = result.Role;
            }

            if (result.Version is not null)
            {
                server.KerioVersion = result.Version;
            }

            if (result.Certificates is not null)
            {
                server.KerioCertificateCount = result.Certificates.Count;
            }

            if (result.Store is not null)
            {
                server.Store = [.. result.Store];
                server.StoreCheckedAt = now;
            }
        }

        if (result.ServedByName is not null)
        {
            server.ServedByName = new Dictionary<string, string>(result.ServedByName, StringComparer.Ordinal);
        }
    }
}
