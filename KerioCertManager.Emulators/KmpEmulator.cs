using System.Globalization;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace KerioCertManager.Emulators;

/// <summary>Выпуск сертификата в эмуляторе Key Manager Plus.</summary>
public sealed record KmpEntry(string CommonName, string Serial, X509Certificate2 Certificate, string Passphrase, DateTimeOffset CreatedAt);

/// <summary>Эмулятор REST API Key Manager Plus 7.1.1 — ровно то, что проверено на настоящем.</summary>
public sealed class KmpEmulator(TestPki pki, string apiKey)
{
    private readonly Lock _замок = new();
    private readonly List<KmpEntry> _выпуски = [];

    public string ApiKey { get; } = apiKey;

    public IReadOnlyList<KmpEntry> Entries
    {
        get
        {
            lock (_замок)
            {
                return [.. _выпуски];
            }
        }
    }

    /// <summary>Выпустить сертификат имени (продление — новый серийный рядом со старым, как в KMP).</summary>
    public KmpEntry Issue(string commonName, TimeSpan validity, TimeSpan? age = null, IReadOnlyList<string>? extraNames = null)
    {
        var начало = DateTimeOffset.UtcNow - (age ?? TimeSpan.FromMinutes(5));
        var имена = new List<string> { commonName };
        имена.AddRange(extraNames ?? []);

        var сертификат = pki.Issue(имена, начало, начало + validity);
        var выпуск = new KmpEntry(commonName, сертификат.SerialNumber, сертификат, $"pass-{Guid.NewGuid():N}"[..17], DateTimeOffset.UtcNow);

        lock (_замок)
        {
            _выпуски.Add(выпуск);
        }

        return выпуск;
    }

    public void Remove(string serial)
    {
        lock (_замок)
        {
            _выпуски.RemoveAll(в => в.Serial == serial);
        }
    }

    internal IResult Handle(HttpContext context, string method)
    {
        var запрос = context.Request;

        if (!запрос.Headers.TryGetValue("AUTHTOKEN", out var ключ) || ключ.ToString() != ApiKey)
        {
            return Results.Json(new JsonObject
            {
                ["name"] = "AuthenticationFailure",
                ["result"] = new JsonObject { ["status"] = "Failed", ["message"] = "Login Failed!" },
            });
        }

        JsonObject? детали = null;

        if (запрос.Query.TryGetValue("INPUT_DATA", out var вход))
        {
            try
            {
                детали = JsonNode.Parse(вход.ToString())?["operation"]?["Details"] as JsonObject;
            }
            catch (JsonException)
            {
                return Results.Text("INVALID_INPUT", statusCode: 400);
            }
        }

        return method switch
        {
            "getAllSSLCertsExpiryDate" when детали is not null => Results.Text("EXTRA_PARAM_FOUND", statusCode: 400),
            "getAllSSLCertsExpiryDate" => Список(),
            "getCertificate" => Файл(детали),
            "getCertificatePassphrase" => Пароль(детали),
            _ => Results.Text("INVALID_METHOD", statusCode: 400),
        };
    }

    private IResult Список()
    {
        var строки = new JsonArray();

        foreach (var в in Entries)
        {
            строки.Add(new JsonObject
            {
                ["Common Name"] = в.CommonName,
                ["serialNumber"] = в.Serial.ToLowerInvariant(),
                ["ExpiryDate"] = в.Certificate.NotAfter.ToUniversalTime().ToString("MMM d, yyyy", CultureInfo.InvariantCulture),
                ["Issuer"] = "Kerio Cert Manager Demo Issuing CA",
            });
        }

        return Results.Json(new JsonObject
        {
            ["name"] = "GetAllSSLCertsExpiryDate",
            ["result"] = new JsonObject { ["status"] = "Success", ["message"] = "Fetched" },
            ["totalRows"] = строки.Count,
            ["details"] = строки,
        });
    }

    private KmpEntry? Найти(JsonObject? детали)
    {
        var имя = детали?["common_name"]?.GetValue<string>();
        var серийный = детали?["serial_number"]?.GetValue<string>();

        return Entries.FirstOrDefault(в =>
            string.Equals(в.CommonName, имя, StringComparison.OrdinalIgnoreCase)
            && string.Equals(в.Serial, серийный, StringComparison.OrdinalIgnoreCase));
    }

    private IResult Файл(JsonObject? детали)
    {
        var выпуск = Найти(детали);

        if (выпуск is null)
        {
            return Results.Json(new JsonObject
            {
                ["name"] = "GetCertificate",
                ["result"] = new JsonObject { ["status"] = "Failed", ["message"] = "Certificate not found" },
            });
        }

        var тип = детали?["fileType"]?.GetValue<string>()?.ToUpperInvariant();
        var сертификат = выпуск.Certificate;

        byte[] байты = тип switch
        {
            "PEM" => Encoding.ASCII.GetBytes(сертификат.ExportCertificatePem()),
            "CRT" or "CER" or "DER" => сертификат.RawData,
            "P7B" => new X509Certificate2Collection(new[] { X509CertificateLoader.LoadCertificate(сертификат.RawData), pki.Intermediate, pki.Root })
                .Export(X509ContentType.Pkcs7)!,
            "KEY" => Encoding.ASCII.GetBytes(
                сертификат.GetRSAPrivateKey()!.ExportEncryptedPkcs8PrivateKeyPem(
                    выпуск.Passphrase,
                    new PbeParameters(PbeEncryptionAlgorithm.Aes256Cbc, HashAlgorithmName.SHA256, 10_000))),
            "PKCS" => сертификат.Export(X509ContentType.Pkcs12, выпуск.Passphrase),
            _ => [],
        };

        return байты.Length == 0
            ? Results.Json(new JsonObject
            {
                ["name"] = "GetCertificate",
                ["result"] = new JsonObject { ["status"] = "Failed", ["message"] = $"Unsupported fileType {тип}" },
            })
            : Results.Bytes(байты, "application/octet-stream", выпуск.CommonName);
    }

    private IResult Пароль(JsonObject? детали)
    {
        var выпуск = Найти(детали);

        return Results.Json(new JsonObject
        {
            ["name"] = "GetCertificatePassphrase",
            ["result"] = new JsonObject
            {
                ["status"] = выпуск is null ? "Failed" : "Success",
                ["message"] = выпуск is null ? "Certificate not found" : "Private key passphrase is fetched",
            },
            ["details"] = выпуск is null ? null : new JsonObject { ["Passphrase"] = выпуск.Passphrase },
        });
    }
}
