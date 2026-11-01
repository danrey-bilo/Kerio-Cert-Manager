using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace KerioCertManager.Modules.Kerio.Client;

/// <summary>Вид отказа Kerio — по нему решают повтор, письмо и текст в панели.</summary>
internal enum KerioFailure
{
    /// <summary>Сеть, тайм-аут, не тот ответ HTTP.</summary>
    Transport,

    /// <summary>Сертификат Kerio не прошёл проверку доверия.</summary>
    Tls,

    /// <summary>Kerio не принял логин или пароль.</summary>
    Authentication,

    /// <summary>У учётной записи нет прав.</summary>
    Forbidden,

    /// <summary>Метода нет или параметры не те: другая версия Kerio.</summary>
    Protocol,

    /// <summary>Kerio отказал в операции с объяснением.</summary>
    Rejected,
}

/// <summary>Отказ Kerio Connect.</summary>
internal sealed class KerioException(KerioFailure kind, string message, int? code = null, Exception? inner = null)
    : Exception(message, inner)
{
    public KerioFailure Kind { get; } = kind;

    /// <summary>Код ошибки JSON-RPC Kerio.</summary>
    public int? Code { get; } = code;
}

/// <summary>Сертификат из хранилища Kerio (<c>ConnectCertificate.get</c>).</summary>
internal sealed record KerioCertificate(
    string Id,
    string Type,
    string Subject,
    string Issuer,
    DateOnly? ValidTo,
    string ValidType,
    bool IsSelfSigned,
    bool IsUntrusted,
    IReadOnlyList<string> Chain)
{
    /// <summary>Действующий сертификат, не по умолчанию.</summary>
    public const string Active = "ActiveCertificate";

    public const string Inactive = "InactiveCertificate";

    /// <summary>Сертификат по умолчанию: его Kerio подаёт без SNI и для имён без своего сертификата.</summary>
    public const string Default = "ServerCertificate";

    /// <summary>Идентификатор встроенного самоподписанного сертификата Kerio.</summary>
    public const string BuiltInId = "server";

    public bool IsDefault => Type == Default;

    /// <summary>Годится в серверные: не запрос, не удостоверяющий центр.</summary>
    public bool IsServerCertificate => Type is Active or Inactive or Default;
}

/// <summary>Учётная запись сессии.</summary>
internal sealed record KerioIdentity(string LoginName, string Role);

/// <summary>Итог <c>importPrivateKey</c>.</summary>
internal sealed record KerioKeyImport(string Id, bool NeedPassword);

/// <summary>Сессия администрирования Kerio Connect.</summary>
internal interface IKerioSession : IAsyncDisposable
{
    Task<KerioIdentity> WhoAmIAsync(CancellationToken cancellationToken = default);

    /// <summary>Версия Kerio; <c>null</c> — Kerio не сказал.</summary>
    Task<string?> VersionAsync(CancellationToken cancellationToken = default);

    Task<IReadOnlyList<KerioCertificate>> CertificatesAsync(CancellationToken cancellationToken = default);

    /// <summary>Сертификат текстом PEM (<c>toSource</c>).</summary>
    Task<string> SourceAsync(string id, CancellationToken cancellationToken = default);

    /// <summary>Загрузить файл; вернуть его <c>fileId</c>.</summary>
    Task<string> UploadAsync(byte[] content, string fileName, CancellationToken cancellationToken = default);

    Task<KerioKeyImport> ImportPrivateKeyAsync(string fileId, CancellationToken cancellationToken = default);

    Task ImportCertificateAsync(string keyId, string fileId, string password, CancellationToken cancellationToken = default);

    Task SetActiveAsync(string id, CancellationToken cancellationToken = default);

    /// <summary>Удалить сертификаты; вернуть тексты отказов.</summary>
    Task<IReadOnlyList<string>> RemoveAsync(IReadOnlyList<string> ids, CancellationToken cancellationToken = default);
}

/// <summary>JSON-RPC 2.0 администрирования Kerio Connect: <c>/admin/api/jsonrpc/</c>.</summary>
internal sealed class KerioSession : IKerioSession
{
    internal const string Путь = "admin/api/jsonrpc/";

    internal const string Приложение = "Kerio Cert Manager";

    /// <summary>Сколько ждать, пока Kerio закончит перезапуск HTTPS, прежде чем считать его недоступным.</summary>
    internal static TimeSpan ОжиданиеПерезапуска { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>Первый вход ждёт меньше: выключенный Kerio не держит проверку соединения в панели минуту.</summary>
    internal static TimeSpan ОжиданиеПервогоВхода { get; set; } = TimeSpan.FromSeconds(15);

    private static readonly TimeSpan ШагВхода = TimeSpan.FromSeconds(3);

    private readonly HttpClient _http;
    private string? _token;
    private int _номер;

    /// <summary>Для повторного входа; живёт не дольше сессии.</summary>
    private (string Username, string Password, string Version)? _учётка;

    private KerioSession(HttpClient http) => _http = http;

    /// <summary>Войти.</summary>
    public static async Task<KerioSession> LoginAsync(
        HttpClient http, string username, string password, string version, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(http);

        var сессия = new KerioSession(http);

        try
        {
            сессия._учётка = (username, password, version);
            await сессия.ВойтиСноваAsync(ОжиданиеПервогоВхода, cancellationToken);

            return сессия;
        }
        catch
        {
            http.Dispose();
            throw;
        }
    }

    private async Task ВойтиAsync(string username, string password, string version, CancellationToken cancellationToken)
    {
        _token = null;

        var итог = await ОдинРазAsync(
            "Session.login",
            new JsonObject
            {
                ["userName"] = username,
                ["password"] = password,
                ["application"] = new JsonObject
                {
                    ["name"] = Приложение,
                    ["vendor"] = "EAMS",
                    ["version"] = version,
                },
            },
            cancellationToken);

        _token = итог?["token"]?.GetValue<string>()
            ?? throw new KerioException(KerioFailure.Protocol, "Kerio ответил на вход без токена сессии.");
    }

    /// <summary>Войти снова после перезапуска HTTPS: ждать, пока порт ответит, не дольше <paramref name="ждать"/>.</summary>
    private async Task ВойтиСноваAsync(TimeSpan ждать, CancellationToken cancellationToken)
    {
        var (логин, пароль, версия) = _учётка ?? throw new InvalidOperationException("Сессия без учётных данных.");
        var до = DateTimeOffset.UtcNow + ждать;

        while (true)
        {
            try
            {
                await ВойтиAsync(логин, пароль, версия, cancellationToken);
                return;
            }
            catch (KerioException ошибка) when (Временная(ошибка) && DateTimeOffset.UtcNow + ШагВхода < до)
            {
                await Task.Delay(ШагВхода, cancellationToken);
            }
        }
    }

    /// <summary>Отказ, который пройдёт сам: истёкшая сессия или порт, ещё не поднявшийся после перезапуска.</summary>
    internal static bool Временная(KerioException ошибка) =>
        ошибка.Code == -32001
        || (ошибка.Kind == KerioFailure.Transport
            && ошибка.InnerException is HttpRequestException { HttpRequestError: HttpRequestError.ConnectionError });

    public async Task<KerioIdentity> WhoAmIAsync(CancellationToken cancellationToken = default)
    {
        var итог = await ВызватьAsync("Session.whoAmI", new JsonObject(), cancellationToken);
        var пользователь = итог?["userDetails"];

        return new KerioIdentity(
            Строка(пользователь?["loginName"]) ?? string.Empty,
            Строка(пользователь?["effectiveRole"]?["userRole"]) ?? string.Empty);
    }

    public async Task<string?> VersionAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var итог = await ВызватьAsync("Server.getProductInfo", new JsonObject(), cancellationToken);
            var сведения = итог?["info"];
            var версия = Строка(сведения?["version"]);
            var сборка = Строка(сведения?["buildNumber"]);

            return версия is null ? null : сборка is null ? версия : $"{версия} (build {сборка})";
        }
        catch (KerioException ошибка) when (ошибка.Kind is KerioFailure.Forbidden or KerioFailure.Protocol)
        {
            return null;
        }
    }

    public async Task<IReadOnlyList<KerioCertificate>> CertificatesAsync(CancellationToken cancellationToken = default)
    {
        var итог = await ВызватьAsync("ConnectCertificate.get", new JsonObject(), cancellationToken);

        if (итог?["certificates"] is not JsonArray список)
        {
            throw new KerioException(KerioFailure.Protocol, "Kerio ответил на запрос сертификатов без списка certificates.");
        }

        return [.. список.OfType<JsonObject>().Select(Разобрать)];
    }

    public async Task<string> SourceAsync(string id, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);

        var итог = await ВызватьAsync("ConnectCertificate.toSource", new JsonObject { ["id"] = id }, cancellationToken);

        return Строка(итог?["source"])
            ?? throw new KerioException(KerioFailure.Protocol, $"Kerio не отдал текст сертификата {id}.");
    }

    public async Task<string> UploadAsync(byte[] content, string fileName, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(content);

        async Task<JsonObject> ЗагрузитьAsync()
        {
            using var форма = new MultipartFormDataContent($"----kcm{Guid.NewGuid():N}");
            var файл = new ByteArrayContent(content);
            файл.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            форма.Add(файл, "file", fileName);

            using var запрос = new HttpRequestMessage(HttpMethod.Post, Путь + "upload/") { Content = форма };
            Подписать(запрос);

            return await ОтправитьAsync(запрос, "загрузка файла", cancellationToken);
        }

        JsonObject корень;

        try
        {
            корень = await ЗагрузитьAsync();
        }
        catch (KerioException ошибка) when (_учётка is not null && Временная(ошибка))
        {
            await ВойтиСноваAsync(ОжиданиеПерезапуска, cancellationToken);
            корень = await ЗагрузитьAsync();
        }

        return Строка(корень["result"]?["fileUpload"]?["id"])
            ?? throw new KerioException(KerioFailure.Protocol, "Kerio принял файл, но не вернул его идентификатор.");
    }

    public async Task<KerioKeyImport> ImportPrivateKeyAsync(string fileId, CancellationToken cancellationToken = default)
    {
        var итог = await ВызватьAsync(
            "ConnectCertificate.importPrivateKey", new JsonObject { ["fileId"] = fileId }, cancellationToken);

        var id = Строка(итог?["id"])
            ?? throw new KerioException(KerioFailure.Protocol, "Kerio принял ключ, но не вернул его идентификатор.");

        return new KerioKeyImport(id, итог?["needPassword"]?.GetValueKind() == JsonValueKind.True);
    }

    public Task ImportCertificateAsync(
        string keyId, string fileId, string password, CancellationToken cancellationToken = default) =>
        ВызватьAsync(
            "ConnectCertificate.importCertificate",
            new JsonObject { ["id"] = keyId, ["fileId"] = fileId, ["password"] = password },
            cancellationToken);

    public Task SetActiveAsync(string id, CancellationToken cancellationToken = default) =>
        ВызватьAsync("ConnectCertificate.setActive", new JsonObject { ["id"] = id }, cancellationToken);

    public async Task<IReadOnlyList<string>> RemoveAsync(IReadOnlyList<string> ids, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(ids);

        var итог = await ВызватьAsync(
            "ConnectCertificate.remove",
            new JsonObject { ["certificateIds"] = new JsonArray([.. ids.Select(id => (JsonNode)id)]) },
            cancellationToken);

        return итог?["errors"] is JsonArray отказы
            ? [.. отказы.Select(о => Строка(о?["message"]) ?? Строка(о?["errorMessage"]) ?? о?.ToJsonString() ?? string.Empty)]
            : [];
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (_token is not null)
            {
                using var срок = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                await ВызватьAsync("Session.logout", new JsonObject(), срок.Token);
            }
        }
        catch (Exception ошибка) when (ошибка is KerioException or OperationCanceledException or HttpRequestException)
        {
        }
        finally
        {
            _token = null;
            _учётка = null;
            _http.Dispose();
        }
    }

    /// <summary>Вызов с повторным входом: истекла сессия или Kerio перезапускает HTTPS — войти снова и повторить один раз.</summary>
    private async Task<JsonNode?> ВызватьAsync(string метод, JsonObject параметры, CancellationToken cancellationToken)
    {
        try
        {
            return await ОдинРазAsync(метод, параметры, cancellationToken);
        }
        catch (KerioException ошибка) when (_учётка is not null && метод != "Session.logout" && Временная(ошибка))
        {
            await ВойтиСноваAsync(ОжиданиеПерезапуска, cancellationToken);
            return await ОдинРазAsync(метод, (JsonObject)параметры.DeepClone(), cancellationToken);
        }
    }

    private async Task<JsonNode?> ОдинРазAsync(string метод, JsonObject параметры, CancellationToken cancellationToken)
    {
        var тело = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = Interlocked.Increment(ref _номер),
            ["method"] = метод,
            ["params"] = параметры,
        };

        if (_token is not null)
        {
            тело["token"] = _token;
        }

        using var запрос = new HttpRequestMessage(HttpMethod.Post, Путь)
        {
            Content = new StringContent(тело.ToJsonString(), Encoding.UTF8),
        };

        запрос.Content.Headers.ContentType = MediaTypeHeaderValue.Parse("application/json-rpc; charset=UTF-8");
        запрос.Headers.Accept.ParseAdd("application/json-rpc");
        Подписать(запрос);

        var корень = await ОтправитьAsync(запрос, метод, cancellationToken);

        return корень["result"];
    }

    private void Подписать(HttpRequestMessage запрос)
    {
        if (_token is not null)
        {
            запрос.Headers.TryAddWithoutValidation("X-Token", _token);
        }
    }

    private async Task<JsonObject> ОтправитьAsync(HttpRequestMessage запрос, string что, CancellationToken cancellationToken)
    {
        HttpResponseMessage ответ;

        try
        {
            ответ = await _http.SendAsync(запрос, HttpCompletionOption.ResponseContentRead, cancellationToken);
        }
        catch (HttpRequestException ошибка) when (Tls(ошибка))
        {
            throw new KerioException(
                KerioFailure.Tls,
                "Сертификат Kerio не прошёл проверку доверия: он сменился или не совпадает с закреплённым. Проверьте "
                + "отпечаток в настройках сервера.",
                inner: ошибка);
        }
        catch (HttpRequestException ошибка)
        {
            throw new KerioException(KerioFailure.Transport, $"Kerio недоступен: {Сеть(ошибка)}.", inner: ошибка);
        }
        catch (TaskCanceledException ошибка) when (!cancellationToken.IsCancellationRequested)
        {
            throw new KerioException(KerioFailure.Transport, $"Kerio не ответил вовремя ({что}).", inner: ошибка);
        }

        using (ответ)
        {
            if ((int)ответ.StatusCode is >= 300 and < 400)
            {
                throw new KerioException(
                    KerioFailure.Transport,
                    "Kerio ответил перенаправлением — проверьте адрес: перенаправлениям сервис не следует, иначе пароль ушёл бы по чужому адресу.");
            }

            if (ответ.StatusCode != HttpStatusCode.OK)
            {
                throw new KerioException(
                    ответ.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden ? KerioFailure.Forbidden : KerioFailure.Transport,
                    $"Kerio ответил HTTP {(int)ответ.StatusCode} ({что}).");
            }

            JsonNode? узел;

            try
            {
                узел = JsonNode.Parse(await ответ.Content.ReadAsStringAsync(cancellationToken));
            }
            catch (JsonException)
            {
                throw new KerioException(KerioFailure.Protocol, $"Kerio ответил не JSON-RPC ({что}) — это точно Kerio Connect?");
            }

            if (узел is not JsonObject корень)
            {
                throw new KerioException(KerioFailure.Protocol, $"Kerio ответил не объектом JSON-RPC ({что}).");
            }

            if (корень["error"] is JsonObject ошибка)
            {
                throw Отказ(ошибка, что);
            }

            return корень;
        }
    }

    /// <summary>Ошибка JSON-RPC Kerio в отказ с видом.</summary>
    internal static KerioException Отказ(JsonObject ошибка, string что)
    {
        var код = ошибка["code"]?.GetValueKind() == JsonValueKind.Number ? ошибка["code"]!.GetValue<int>() : (int?)null;
        var текст = Строка(ошибка["message"]) ?? "без объяснения";

        return код switch
        {
            1000 when что == "Session.login" => new KerioException(
                KerioFailure.Authentication, $"Kerio не принял логин или пароль: {текст}", код),
            // ⚠ Не отказ пароля: так Kerio отвечает и после перезапуска HTTPS.
            -32001 => new KerioException(KerioFailure.Transport, "Сессия Kerio истекла — Kerio перезапустил администрирование.", код),
            1004 => new KerioException(
                KerioFailure.Forbidden,
                "У учётной записи Kerio нет прав на эту операцию: нужна роль администратора с правом на сертификаты.",
                код),
            -32601 => new KerioException(
                KerioFailure.Protocol, $"Kerio не знает метода {что} — версия Kerio не та, что ожидает сервис.", код),
            -32602 => new KerioException(KerioFailure.Protocol, $"Kerio не принял параметры {что}: {текст}", код),
            _ => new KerioException(
                KerioFailure.Rejected,
                $"Kerio отказал ({что}): {текст}" + (код is { } к ? $" [код {к.ToString(CultureInfo.InvariantCulture)}]" : string.Empty),
                код),
        };
    }

    internal static KerioCertificate Разобрать(JsonObject с)
    {
        var срок = с["validPeriod"]?["validToDate"];
        DateOnly? до = null;

        if (срок is JsonObject дата
            && Число(дата["year"]) is { } год
            && Число(дата["month"]) is { } месяц
            && Число(дата["day"]) is { } день)
        {
            // ⚠ Месяц у Kerio с нуля (проверено на 10.0.6).
            try
            {
                до = new DateOnly(год, месяц + 1, день);
            }
            catch (ArgumentOutOfRangeException)
            {
                до = null;
            }
        }

        return new KerioCertificate(
            Строка(с["id"]) ?? string.Empty,
            Строка(с["type"]) ?? string.Empty,
            Имя(с["subject"]),
            Имя(с["issuer"]),
            до,
            Строка(с["validPeriod"]?["validType"]) ?? string.Empty,
            с["isSelfSigned"]?.GetValueKind() == JsonValueKind.True,
            с["isUntrusted"]?.GetValueKind() == JsonValueKind.True,
            с["chainInfo"] is JsonArray цепь ? [.. цепь.Select(Строка).OfType<string>()] : []);
    }

    private static string Имя(JsonNode? список) =>
        список is JsonArray пары
            ? пары.OfType<JsonObject>().Where(п => Строка(п["name"]) == "hostname").Select(п => Строка(п["value"])).FirstOrDefault() ?? string.Empty
            : string.Empty;

    private static int? Число(JsonNode? узел) =>
        узел?.GetValueKind() == JsonValueKind.Number ? узел.GetValue<int>() : null;

    private static string? Строка(JsonNode? узел) =>
        узел?.GetValueKind() == JsonValueKind.String ? узел.GetValue<string>() : null;

    private static bool Tls(HttpRequestException ошибка) =>
        ошибка.HttpRequestError == HttpRequestError.SecureConnectionError
        || ошибка.InnerException is System.Security.Authentication.AuthenticationException;

    private static string Сеть(HttpRequestException ошибка) => ошибка.HttpRequestError switch
    {
        HttpRequestError.NameResolutionError => "имя сервера не разрешается",
        HttpRequestError.ConnectionError => "не удалось соединиться",
        HttpRequestError.SecureConnectionError => "TLS-соединение не установилось",
        _ => "сетевая ошибка",
    };
}
