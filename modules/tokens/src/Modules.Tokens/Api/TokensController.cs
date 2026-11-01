using System.Text.Json.Serialization;
using Modules.Tokens.Permissions;
using Modules.Tokens.Tokens;
using Modules.Core.Contracts.Authz;
using Microsoft.AspNetCore.Mvc;

namespace Modules.Tokens.Api;

/// <summary>Строка инвентаря: чем человек предъявляется системе.</summary>
internal sealed record TokenRowOut(
    [property: JsonPropertyName("id")] Guid Id,
    [property: JsonPropertyName("user_id")] Guid UserId,
    [property: JsonPropertyName("user_name")] string? UserName,
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("name")] string? Name,
    [property: JsonPropertyName("scopes")] IReadOnlyList<string> Scopes,
    [property: JsonPropertyName("created_at")] DateTimeOffset CreatedAt,
    [property: JsonPropertyName("last_seen_at")] DateTimeOffset LastSeenAt,
    [property: JsonPropertyName("expires_at")] DateTimeOffset ExpiresAt,
    [property: JsonPropertyName("revoked_at")] DateTimeOffset? RevokedAt);

/// <summary>Чужие предъявляемые секреты: инвентарь и точечный отзыв.</summary>
[ApiController]
[Route("api/tokens")]
[Modules.Core.Features.Feature("tokens.inventory")]
internal sealed class TokensController(TokenInventory inventory) : ControllerBase
{
    /// <summary>Кто чем предъявляется.</summary>
    [HttpGet]
    [RequirePermission(ApiTokenPermissions.Read)]
    public async Task<IReadOnlyList<TokenRowOut>> List(
        [FromQuery(Name = "user_id")] Guid? userId,
        [FromQuery(Name = "kind")] string? kind,
        [FromQuery(Name = "include_revoked")] bool includeRevoked,
        CancellationToken cancellationToken)
    {
        var строки = await inventory.ListAsync(userId, kind, includeRevoked, cancellationToken);

        return [.. строки.Select(строка => new TokenRowOut(
            строка.Id,
            строка.UserId,
            строка.UserName,
            строка.Kind,
            строка.Name,
            строка.Scopes,
            строка.CreatedAt,
            строка.LastSeenAt,
            строка.ExpiresAt,
            строка.RevokedAt))];
    }

    /// <summary>Закрыть одну чужую строку.</summary>
    [HttpDelete("{id:guid}")]
    [AuthenticatedEndpoint(
        "право зависит от вида строки и потому спрашивается службой: ключ — apitoken.revoke, "
        + "сессия — token.revoke. Атрибут вида не знает, пока строка не прочитана")]
    public async Task<IActionResult> Revoke(Guid id, CancellationToken cancellationToken)
    {
        await inventory.RevokeAsync(id, cancellationToken);

        return NoContent();
    }
}
