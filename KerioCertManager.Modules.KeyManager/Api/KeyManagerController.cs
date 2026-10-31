using System.Net;
using System.Text.Json.Serialization;
using KerioCertManager.Modules.KeyManager.Client;
using KerioCertManager.Modules.KeyManager.Contracts;
using KerioCertManager.Modules.KeyManager.Source;
using KerioCertManager.Modules.KeyManager.Domain;
using KerioCertManager.Modules.KeyManager.Secrets;
using Modules.Core.Contracts.Audit;
using Modules.Core.Contracts.Authz;
using Modules.Core.Data;
using Modules.Core.Errors;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace KerioCertManager.Modules.KeyManager.Api;

/// <summary>Состояние связи с Key Manager Plus.</summary>
internal sealed record KeyManagerStatusOut(
    [property: JsonPropertyName("enabled")] bool Enabled,
    [property: JsonPropertyName("configured")] bool Configured,
    [property: JsonPropertyName("api_key_set")] bool ApiKeySet,
    [property: JsonPropertyName("last_poll_at")] DateTimeOffset? LastPollAt,
    [property: JsonPropertyName("last_poll_error")] string? LastPollError,
    [property: JsonPropertyName("last_poll_count")] int? LastPollCount);

/// <summary>Новый ключ API.</summary>
internal sealed record ApiKeyIn([property: JsonPropertyName("api_key")] string? ApiKey);

/// <summary>Итог проверки связи.</summary>
internal sealed record KeyManagerTestOut([property: JsonPropertyName("certificates")] int Certificates);

/// <summary>Сертификат из последнего опроса.</summary>
internal sealed record KnownCertificateOut(
    [property: JsonPropertyName("common_name")] string CommonName,
    [property: JsonPropertyName("serial_number")] string SerialNumber,
    [property: JsonPropertyName("expires_on")] DateOnly? ExpiresOn,
    [property: JsonPropertyName("first_seen_at")] DateTimeOffset FirstSeenAt,
    [property: JsonPropertyName("last_seen_at")] DateTimeOffset LastSeenAt);

/// <summary>Связь с Key Manager Plus: состояние, ключ API, проверка, опрос, список сертификатов.</summary>
[ApiController]
[AuthenticatedEndpoint("ролей в проекте нет: каждая учётная запись — администратор Key Manager Plus")]
[Route("api/keymanager")]
internal sealed class KeyManagerController(
    AppDbContext db,
    ApiKeyStore apiKey,
    IKeyManagerConnector connector,
    SourceWakeup wakeup,
    IAuditWriter audit) : ControllerBase
{
    /// <summary>Состояние: включено ли, задан ли ключ, чем кончился последний опрос.</summary>
    [HttpGet]
    public async Task<KeyManagerStatusOut> Status(CancellationToken cancellationToken)
    {
        var настройки = await connector.ReadAsync(cancellationToken);

        var состояние = await db.Set<KeyManagerState>()
            .AsNoTracking()
            .FirstOrDefaultAsync(с => с.Id == KeyManagerState.SingletonId, cancellationToken);

        return new KeyManagerStatusOut(
            настройки.Enabled,
            настройки.Configured,
            настройки.ApiKeySet,
            состояние?.LastPollAt,
            состояние?.LastPollError,
            состояние?.LastPollCount);
    }

    /// <summary>Задать или снять ключ API (Settings → General Settings → API Key в Key Manager Plus).</summary>
    [HttpPut("api-key")]
    [RequireRecentSecondFactor]
    public async Task<IActionResult> SetApiKey([FromBody] ApiKeyIn body, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(body);

        var настройки = await connector.ReadAsync(cancellationToken);

        await db.InTransactionAsync(
            async token =>
            {
                await apiKey.PutAsync(
                    body.ApiKey, ApiKeyStore.Target(настройки.BaseUrl), token);

                audit.Write(new AuditEntry(
                    KeyManagerActions.ApiKeySet,
                    Changes: new Dictionary<string, object?>(StringComparer.Ordinal)
                    {
                        ["set"] = !string.IsNullOrWhiteSpace(body.ApiKey),
                    }));

                await db.SaveChangesAsync(token);
            },
            cancellationToken);

        return NoContent();
    }

    /// <summary>Проверить связь живым запросом списка; работает и при выключенной интеграции.</summary>
    [HttpPost("test")]
    public async Task<KeyManagerTestOut> Test(CancellationToken cancellationToken)
    {
        try
        {
            using var клиент = await connector.OpenAsync(cancellationToken);

            return new KeyManagerTestOut((await клиент.ListAsync(cancellationToken)).Count);
        }
        catch (KeyManagerException ошибка)
        {
            throw Unavailable(ошибка);
        }
    }

    /// <summary>Опросить Key Manager Plus и проверить серверы сейчас, не дожидаясь срока.</summary>
    [HttpPost("poll")]
    public IActionResult Poll()
    {
        wakeup.Signal();

        return Accepted();
    }

    /// <summary>Сертификаты из последнего опроса: по Common Name, замеченные позже — первыми.</summary>
    [HttpGet("certificates")]
    public async Task<IReadOnlyList<KnownCertificateOut>> Certificates(CancellationToken cancellationToken) =>
        await CertificateSnapshots.Current(db)
            .AsNoTracking()
            .OrderBy(с => с.CommonName)
            .ThenByDescending(с => с.FirstSeenAt)
            .ThenByDescending(с => с.ExpiresOn)
            .Select(с => new KnownCertificateOut(
                с.CommonName, с.SerialNumber, с.ExpiresOn, с.FirstSeenAt, с.LastSeenAt))
            .ToListAsync(cancellationToken);

    /// <summary>Отказ Key Manager Plus наружу.</summary>
    internal static AppException Unavailable(KeyManagerException ошибка) => new(
        KeyManagerErrorCodes.Unavailable,
        "Key Manager Plus не ответил или отказал.",
        HttpStatusCode.Conflict,
        new Dictionary<string, object?>(StringComparer.Ordinal) { ["reason"] = ошибка.Message });
}
