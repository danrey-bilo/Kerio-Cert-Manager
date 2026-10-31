using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace KerioCertManager.Emulators;

/// <summary>Сертификат в хранилище эмулятора Kerio.</summary>
public sealed record KerioStoreEntry(string Id, X509Certificate2 Certificate, string ChainPem, bool BuiltIn);

/// <summary>Эмулятор администрирования Kerio Connect 10.0.6: JSON-RPC, загрузка файлов, смена сертификата по TLS.</summary>
public sealed class KerioEmulator
{
    private readonly Lock _замок = new();
    private readonly Dictionary<string, KerioStoreEntry> _хранилище = new(StringComparer.Ordinal);
    private readonly Dictionary<string, byte[]> _файлы = new(StringComparer.Ordinal);
    private readonly Dictionary<string, byte[]> _ключи = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _сессии = new(StringComparer.Ordinal);
    private int _номер = 1;

    public KerioEmulator(string name, int adminPort, IReadOnlyList<int> servicePorts, string user, string password)
    {
        Name = name;
        AdminPort = adminPort;
        ServicePorts = servicePorts;
        User = user;
        Password = password;

        var встроенный = TestPki.SelfSigned("kerio");
        _хранилище["server"] = new KerioStoreEntry("server", встроенный, встроенный.ExportCertificatePem(), true);
        ConfigActiveId = "server";
        ServedId = "server";
    }

    public string Name { get; }

    public int AdminPort { get; }

    public IReadOnlyList<int> ServicePorts { get; }

    public string User { get; }

    public string Password { get; }

    /// <summary>Роль учётной записи: <c>FullAdmin</c> или, для проверки отказа, <c>Auditor</c>.</summary>
    public string Role { get; set; } = "FullAdmin";

    /// <summary>Какой сертификат Kerio считает активным (что видно в списке).</summary>
    public string ConfigActiveId { get; private set; }

    /// <summary>Какой сертификат реально подаётся по TLS.</summary>
    public string ServedId { get; private set; }

    /// <summary>Залипание: порты служб не переключаются на новый активный.</summary>
    public bool Stuck { get; set; }

    /// <summary>Через сколько после импорта или удаления все сессии API истекают — как перезапуск HTTPS у настоящего Kerio (там ~25 с); null — не истекают.</summary>
    public TimeSpan? SessionResetAfterChange { get; set; }

    /// <summary>Сколько раз сессии сбрасывались после изменения хранилища.</summary>
    public int SessionResets { get; private set; }

    /// <summary>Методы, которые отвечают отказом.</summary>
    public HashSet<string> FailingMethods { get; } = new(StringComparer.Ordinal);

    public int Logins { get; private set; }

    public int FailedLogins { get; private set; }

    public IReadOnlyList<KerioStoreEntry> Store
    {
        get
        {
            lock (_замок)
            {
                return [.. _хранилище.Values.OrderBy(з => з.Id, StringComparer.Ordinal)];
            }
        }
    }

    /// <summary>Подаётся встроенный самоподписанный — цепочки нет.</summary>
    public bool ServedIsBuiltIn
    {
        get
        {
            lock (_замок)
            {
                return !_хранилище.TryGetValue(ServedId, out var з) || з.BuiltIn;
            }
        }
    }

    /// <summary>Сертификат, который сейчас подаётся по TLS на всех портах.</summary>
    public X509Certificate2 ServedCertificate
    {
        get
        {
            lock (_замок)
            {
                return (_хранилище.TryGetValue(ServedId, out var з) ? з : _хранилище["server"]).Certificate;
            }
        }
    }

    /// <summary>Сертификат для имени из SNI; пусто — по умолчанию.</summary>
    public (X509Certificate2 Certificate, bool BuiltIn) CertificateFor(string? serverName)
    {
        lock (_замок)
        {
            var поУмолчанию = _хранилище.TryGetValue(ServedId, out var з) ? з : _хранилище["server"];

            if (string.IsNullOrWhiteSpace(serverName))
            {
                return (поУмолчанию.Certificate, поУмолчанию.BuiltIn);
            }

            var имя = serverName.Trim().TrimEnd('.').ToLowerInvariant();

            KerioStoreEntry? Выбрать(Func<string, bool> подходит)
            {
                var кандидаты = _хранилище.Values.Where(е => !е.BuiltIn && Имена(е.Certificate).Any(подходит)).ToList();

                return кандидаты.Count == 0 ? null
                    : кандидаты.OrderBy(е => е.Certificate.NotAfter < DateTime.Now).ThenByDescending(е => е.Certificate.NotAfter).First();
            }

            var выбран = Выбрать(н => н == имя)
                ?? Выбрать(н => н.StartsWith("*.", StringComparison.Ordinal)
                    && имя.EndsWith(н[1..], StringComparison.Ordinal)
                    && !имя[..^(н.Length - 1)].Contains('.', StringComparison.Ordinal))
                ?? поУмолчанию;

            return (выбран.Certificate, выбран.BuiltIn);
        }
    }

    private static IEnumerable<string> Имена(X509Certificate2 сертификат)
    {
        var san = сертификат.Extensions.OfType<X509SubjectAlternativeNameExtension>().FirstOrDefault();
        var имена = san is null ? [] : san.EnumerateDnsNames().ToList();

        if (имена.Count == 0)
        {
            имена.Add(сертификат.GetNameInfo(X509NameType.SimpleName, forIssuer: false));
        }

        return имена.Select(и => и.ToLowerInvariant());
    }

    /// <summary>Как перезапуск HTTPS настоящего Kerio: спустя время после изменения хранилища все сессии истекают.</summary>
    private void СброситьСессииПозже()
    {
        if (SessionResetAfterChange is not { } через)
        {
            return;
        }

        _ = Task.Delay(через).ContinueWith(_ =>
        {
            lock (_замок)
            {
                _сессии.Clear();
                SessionResets++;
            }
        }, TaskScheduler.Default);
    }

    /// <summary>Положить сертификат прямо в хранилище и сделать активным — начальное состояние стенда.</summary>
    public string Seed(X509Certificate2 certificateWithKey, string chainPem)
    {
        lock (_замок)
        {
            var id = $"server{_номер++}";
            _хранилище[id] = new KerioStoreEntry(id, TestPki.ForServer(certificateWithKey), chainPem, false);
            ConfigActiveId = id;
            ServedId = id;

            return id;
        }
    }

    /// <summary>Положить сертификат в хранилище, не делая активным, — как оставленный там вручную.</summary>
    public string Keep(X509Certificate2 certificateWithKey, string chainPem)
    {
        lock (_замок)
        {
            var id = $"server{_номер++}";
            _хранилище[id] = new KerioStoreEntry(id, TestPki.ForServer(certificateWithKey), chainPem, false);

            return id;
        }
    }

    internal async Task<IResult> RpcAsync(HttpContext context)
    {
        JsonObject? запрос;

        try
        {
            запрос = JsonNode.Parse(await new StreamReader(context.Request.Body).ReadToEndAsync()) as JsonObject;
        }
        catch (JsonException)
        {
            return Results.Text("bad request", statusCode: 400);
        }

        var id = запрос?["id"]?.DeepClone() ?? 1;
        var метод = запрос?["method"]?.GetValue<string>() ?? string.Empty;
        var параметры = запрос?["params"] as JsonObject ?? [];

        try
        {
            if (метод == "Session.login")
            {
                return Ответ(context, id, Войти(context, параметры));
            }

            if (!Сессия(context, out var токен))
            {
                throw new RpcError(-32001, "Session expired.");
            }

            if (FailingMethods.Contains(метод))
            {
                throw new RpcError(1000, $"Emulated failure of {метод}.");
            }

            JsonNode результат = метод switch
            {
                "Session.logout" => Выйти(токен),
                "Session.whoAmI" => new JsonObject
                {
                    ["userDetails"] = new JsonObject
                    {
                        ["loginName"] = User,
                        ["fullName"] = "Kerio Cert Manager",
                        ["effectiveRole"] = new JsonObject { ["userRole"] = Role },
                    },
                },
                "Server.getProductInfo" => new JsonObject
                {
                    ["info"] = new JsonObject { ["productName"] = "Kerio Connect", ["version"] = "10.0.6", ["buildNumber"] = "8504-emulator" },
                },
                "ConnectCertificate.get" => new JsonObject { ["certificates"] = Список() },
                "ConnectCertificate.toSource" => new JsonObject { ["source"] = Запись(Строка(параметры, "id")).Certificate.ExportCertificatePem() },
                "ConnectCertificate.importPrivateKey" => ИмпортКлюча(Строка(параметры, "fileId")),
                "ConnectCertificate.importCertificate" => ИмпортСертификата(
                    Строка(параметры, "id"), Строка(параметры, "fileId"), параметры["password"]?.GetValue<string>() ?? string.Empty),
                "ConnectCertificate.setActive" => Активировать(Строка(параметры, "id")),
                "ConnectCertificate.remove" => Удалить(параметры["certificateIds"] as JsonArray ?? []),
                _ => throw new RpcError(-32601, "Method not found."),
            };

            return Ответ(context, id, результат);
        }
        catch (RpcError ошибка)
        {
            return Results.Json(new JsonObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = id.DeepClone(),
                ["error"] = new JsonObject { ["code"] = ошибка.Code, ["message"] = ошибка.Message },
            });
        }
    }

    internal async Task<IResult> UploadAsync(HttpContext context)
    {
        if (!Сессия(context, out _))
        {
            return Results.Json(new JsonObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = 1,
                ["error"] = new JsonObject { ["code"] = -32001, ["message"] = "Session expired." },
            });
        }

        var форма = await context.Request.ReadFormAsync();
        var файл = форма.Files.FirstOrDefault();

        if (файл is null)
        {
            return Results.Text("no file", statusCode: 400);
        }

        using var буфер = new MemoryStream();
        await файл.CopyToAsync(буфер);

        var fileId = Convert.ToHexString(RandomNumberGenerator.GetBytes(5)).ToLowerInvariant();

        lock (_замок)
        {
            _файлы[fileId] = буфер.ToArray();
        }

        return Results.Json(new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = 1,
            ["result"] = new JsonObject
            {
                ["fileUpload"] = new JsonObject { ["id"] = fileId, ["length"] = буфер.Length, ["name"] = файл.FileName },
            },
        });
    }

    private JsonNode Войти(HttpContext context, JsonObject параметры)
    {
        var логин = параметры["userName"]?.GetValue<string>();
        var пароль = параметры["password"]?.GetValue<string>();

        if (!string.Equals(логин, User, StringComparison.OrdinalIgnoreCase) || пароль != Password)
        {
            FailedLogins++;
            throw new RpcError(1000, "Username or password is not correct.");
        }

        Logins++;

        var токен = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
        var кука = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();

        lock (_замок)
        {
            _сессии[токен] = кука;
        }

        context.Response.Headers.Append("Set-Cookie", $"SESSION_CONNECT_WEBADMIN={кука}; path=/admin/; secure; HttpOnly");

        return new JsonObject { ["token"] = токен };
    }

    private bool Сессия(HttpContext context, out string токен)
    {
        токен = context.Request.Headers["X-Token"].ToString();
        var кука = context.Request.Cookies["SESSION_CONNECT_WEBADMIN"];

        lock (_замок)
        {
            return _сессии.TryGetValue(токен, out var своя) && своя == кука;
        }
    }

    private JsonObject Выйти(string токен)
    {
        lock (_замок)
        {
            _сессии.Remove(токен);
        }

        return new JsonObject { ["redirectUrl"] = "/admin/login" };
    }

    private JsonArray Список()
    {
        lock (_замок)
        {
            var список = new JsonArray();

            foreach (var з in _хранилище.Values.OrderBy(з => з.Id, StringComparer.Ordinal))
            {
                var с = з.Certificate;
                var до = с.NotAfter.ToUniversalTime();
                var с_ = с.NotBefore.ToUniversalTime();

                список.Add(new JsonObject
                {
                    ["id"] = з.Id,
                    ["status"] = "StoreStatusClean",
                    ["name"] = string.Empty,
                    ["issuer"] = Имя(с.IssuerName),
                    ["subject"] = Имя(с.SubjectName),
                    ["subjectAlternativeNameList"] = new JsonArray(),
                    ["fingerprint"] = string.Empty,
                    ["fingerprintSha1"] = string.Empty,
                    ["fingerprintSha256"] = string.Empty,
                    ["validPeriod"] = new JsonObject
                    {
                        ["validFromDate"] = new JsonObject { ["year"] = с_.Year, ["month"] = с_.Month - 1, ["day"] = с_.Day },
                        ["validToDate"] = new JsonObject { ["year"] = до.Year, ["month"] = до.Month - 1, ["day"] = до.Day },
                        ["validType"] = до < DateTime.UtcNow ? "Expired" : до < DateTime.UtcNow.AddDays(30) ? "ExpireSoon" : "Valid",
                    },
                    ["valid"] = true,
                    ["type"] = з.Id == ConfigActiveId ? "ServerCertificate" : до < DateTime.UtcNow ? "InactiveCertificate" : "ActiveCertificate",
                    ["isUntrusted"] = false,
                    ["verificationMessage"] = string.Empty,
                    ["chainInfo"] = new JsonArray([.. Цепь(з.ChainPem).Select(н => (JsonNode)н)]),
                    ["isSelfSigned"] = з.BuiltIn,
                });
            }

            return список;
        }
    }

    private JsonObject ИмпортКлюча(string fileId)
    {
        lock (_замок)
        {
            if (!_файлы.Remove(fileId, out var байты))
            {
                throw new RpcError(1002, "Uploaded file does not exist.");
            }

            var текст = Encoding.ASCII.GetString(байты);

            if (!текст.Contains("PRIVATE KEY", StringComparison.Ordinal))
            {
                throw new RpcError(-32602, "Unable to import private key, content is invalid.");
            }

            var keyId = $"tic/{_номер}";
            _ключи[keyId] = байты;

            return new JsonObject { ["needPassword"] = текст.Contains("ENCRYPTED", StringComparison.Ordinal), ["id"] = keyId };
        }
    }

    private JsonObject ИмпортСертификата(string keyId, string fileId, string пароль)
    {
        lock (_замок)
        {
            if (!_ключи.Remove(keyId, out var ключБайты))
            {
                throw new RpcError(1002, "Unable to import certificate, key not found.");
            }

            if (!_файлы.Remove(fileId, out var файл))
            {
                throw new RpcError(1002, "Uploaded file does not exist.");
            }

            var цепь = Encoding.ASCII.GetString(файл);
            X509Certificate2 лист;

            try
            {
                лист = X509Certificate2.CreateFromPem(цепь);
            }
            catch (CryptographicException)
            {
                throw new RpcError(-32602, "Unable to import certificate, the content is invalid.");
            }

            var текстКлюча = Encoding.ASCII.GetString(ключБайты);
            using var rsa = RSA.Create();

            try
            {
                if (текстКлюча.Contains("ENCRYPTED", StringComparison.Ordinal))
                {
                    rsa.ImportFromEncryptedPem(текстКлюча, пароль);
                }
                else
                {
                    rsa.ImportFromPem(текстКлюча);
                }
            }
            catch (CryptographicException)
            {
                throw new RpcError(-32602, "Unable to parse private key with given password!");
            }

            if (!rsa.ExportSubjectPublicKeyInfo().AsSpan().SequenceEqual(лист.GetRSAPublicKey()!.ExportSubjectPublicKeyInfo()))
            {
                throw new RpcError(1000, "Unable to import certificate, key is not valid for imported certificate.");
            }

            if (_хранилище.Values.Any(з => з.Certificate.RawData.AsSpan().SequenceEqual(лист.RawData)))
            {
                throw new RpcError(1000, "Unable to import certificate, certificate already imported.");
            }

            var id = $"server{_номер++}";
            _хранилище[id] = new KerioStoreEntry(id, TestPki.ForServer(лист.CopyWithPrivateKey(rsa)), цепь, false);

            СброситьСессииПозже();

            return [];
        }
    }

    private JsonObject Активировать(string id)
    {
        lock (_замок)
        {
            if (!_хранилище.ContainsKey(id))
            {
                throw new RpcError(1002, "Unable to find certificate.");
            }

            // ⚠ Как настоящий 10.0.6: на текущий по умолчанию — ничего.
            if (id == ConfigActiveId)
            {
                return [];
            }

            ConfigActiveId = id;

            if (!Stuck)
            {
                ServedId = id;
            }

            return [];
        }
    }

    private JsonObject Удалить(JsonArray ids)
    {
        lock (_замок)
        {
            var отказы = new JsonArray();

            foreach (var узел in ids)
            {
                var id = узел?.GetValue<string>() ?? string.Empty;

                var номер = ids.IndexOf(узел);

                if (id == ConfigActiveId)
                {
                    отказы.Add(new JsonObject { ["inputIndex"] = номер, ["code"] = 1003, ["message"] = "Cannot delete the default certificate." });
                    continue;
                }

                if (id == "server" || !_хранилище.ContainsKey(id))
                {
                    отказы.Add(new JsonObject { ["inputIndex"] = номер, ["code"] = 1002, ["message"] = "Certificate not found or built-in." });
                    continue;
                }

                _хранилище.Remove(id);

                if (id == ServedId)
                {
                    ServedId = ConfigActiveId;
                }
            }

            if (отказы.Count < ids.Count)
            {
                СброситьСессииПозже();
            }

            return new JsonObject { ["errors"] = отказы };
        }
    }

    private KerioStoreEntry Запись(string id)
    {
        lock (_замок)
        {
            return _хранилище.TryGetValue(id, out var з) ? з : throw new RpcError(1002, "Unable to find certificate.");
        }
    }

    private static IResult Ответ(HttpContext context, JsonNode id, JsonNode результат) =>
        Results.Json(new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id.DeepClone(), ["result"] = результат });

    private static string Строка(JsonObject параметры, string имя) =>
        параметры[имя]?.GetValue<string>() ?? throw new RpcError(-32602, $"Invalid params: {имя}.");

    private static JsonArray Имя(X500DistinguishedName имя)
    {
        var cn = имя.EnumerateRelativeDistinguishedNames()
            .Where(r => r.GetSingleElementType().Value == "2.5.4.3")
            .Select(r => r.GetSingleElementValue())
            .FirstOrDefault() ?? string.Empty;

        return
        [
            new JsonObject { ["name"] = "hostname", ["value"] = cn },
            new JsonObject { ["name"] = "organizationName", ["value"] = string.Empty },
            new JsonObject { ["name"] = "country", ["value"] = string.Empty },
        ];
    }

    private static IEnumerable<string> Цепь(string pem)
    {
        var имена = new List<string>();
        var коллекция = new X509Certificate2Collection();

        try
        {
            коллекция.ImportFromPem(pem);
        }
        catch (CryptographicException)
        {
            return имена;
        }

        foreach (var с in коллекция.Reverse())
        {
            имена.Add(с.GetNameInfo(X509NameType.SimpleName, false));
        }

        return имена;
    }

    private sealed class RpcError(int code, string message) : Exception(message)
    {
        public int Code { get; } = code;
    }
}
