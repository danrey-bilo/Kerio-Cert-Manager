using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;
using Modules.Tokens.Tokens;
using Modules.Core.Contracts.Authz;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Modules.Tokens.Api;

/// <summary>Ключ в списке.</summary>
internal sealed record ApiKeyOut(
    [property: JsonPropertyName("id")] Guid Id,
    [property: JsonPropertyName("name")] string? Name,
    [property: JsonPropertyName("scopes")] IReadOnlyList<string> Scopes,
    [property: JsonPropertyName("created_at")] DateTimeOffset CreatedAt,
    [property: JsonPropertyName("last_seen_at")] DateTimeOffset LastSeenAt,
    [property: JsonPropertyName("expires_at")] DateTimeOffset ExpiresAt,
    [property: JsonPropertyName("revoked_at")] DateTimeOffset? RevokedAt);

/// <summary>Что просят завести.</summary>
internal sealed record CreateApiKeyIn(
    [property: JsonPropertyName("name")][MaxLength(200)] string? Name,
    [property: JsonPropertyName("scopes")][MaxLength(256)] IReadOnlyList<string>? Scopes,
    [property: JsonPropertyName("days")][Range(1, 3650)] int? Days);

/// <summary>Заведённый ключ.</summary>
internal sealed record CreatedApiKeyOut(
    [property: JsonPropertyName("key")] ApiKeyOut Key,
    [property: JsonPropertyName("secret")] string Secret)
{
    /// <summary>Печать без секретов: запись попадает в лог, в отладчик и в текст исключения.</summary>
    private bool PrintMembers(System.Text.StringBuilder builder)
    {
        builder.Append("секреты скрыты: Secret");

        return true;
    }
}

/// <summary>Свои ключи интеграции.</summary>
[ApiController]
[Route("api/me/tokens")]
[Modules.Core.Features.Feature("tokens.api_keys")]
internal sealed class MeTokensController(ApiKeyService keys) : ControllerBase
{
    /// <summary>Свои ключи.</summary>
    [HttpGet]
    [AuthenticatedEndpoint("свои ключи — действие человека над собой")]
    public async Task<IReadOnlyList<ApiKeyOut>> List(CancellationToken cancellationToken)
    {
        var найденные = await keys.ListAsync(cancellationToken);

        return [.. найденные.Select(Показать)];
    }

    /// <summary>Завести ключ.</summary>
    [HttpPost]
    [AuthenticatedEndpoint("свои ключи — действие человека над собой")]
    [RequireRecentSecondFactor]
    // ⚠ ⚠ `ActionResult<T>` плюс атрибут, а не голый `IActionResult` (вскрытие 34.1).
    [ProducesResponseType(StatusCodes.Status201Created)]
    public async Task<ActionResult<CreatedApiKeyOut>> Create(
        [FromBody] CreateApiKeyIn body, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(body);

        var (ключ, секрет) = await keys.CreateAsync(
            body.Name,
            body.Scopes ?? [],
            body.Days is { } дней ? TimeSpan.FromDays(дней) : null,
            cancellationToken);

        return StatusCode(
            StatusCodes.Status201Created, new CreatedApiKeyOut(Показать(ключ), секрет));
    }

    /// <summary>Отозвать свой ключ.</summary>
    [HttpDelete("{id:guid}")]
    [AuthenticatedEndpoint("отзыв своего ключа — действие человека над собой")]
    public async Task<IActionResult> Revoke(Guid id, CancellationToken cancellationToken)
    {
        await keys.RevokeAsync(id, cancellationToken);

        return NoContent();
    }

    private static ApiKeyOut Показать(ApiKeyView ключ) =>
        new(
            ключ.Id,
            ключ.Name,
            ключ.Scopes,
            ключ.CreatedAt,
            ключ.LastSeenAt,
            ключ.ExpiresAt,
            ключ.RevokedAt);
}
