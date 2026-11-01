using System.Text.Json.Serialization;
using Modules.Tokens.Domain;
using Modules.Tokens.Tokens;
using Modules.Core.Contracts.Authz;
using Modules.Core.Contracts.Tokens;
using Modules.Core.Data;
using Modules.Core.Errors;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Modules.Tokens.Api;

/// <summary>Сессия в списке «где я вошёл».</summary>
internal sealed record SessionOut(
    [property: JsonPropertyName("id")] Guid Id,
    [property: JsonPropertyName("user_agent")] string? UserAgent,
    [property: JsonPropertyName("address")] string? Address,
    [property: JsonPropertyName("created_at")] DateTimeOffset CreatedAt,
    [property: JsonPropertyName("last_seen_at")] DateTimeOffset LastSeenAt,
    [property: JsonPropertyName("expires_at")] DateTimeOffset ExpiresAt,
    [property: JsonPropertyName("current")] bool Current);

/// <summary>Свои входы: «где я вошёл» и «закрыть отсюда».</summary>
[ApiController]
[Route("api/me")]
internal sealed class MeSessionsController(
    TokenService tokens,
    ITokenRevoker revoker,
    AppDbContext db,
    ICurrentUser actor) : ControllerBase
{
    /// <summary>Где я вошёл.</summary>
    [HttpGet("sessions")]
    [AuthenticatedEndpoint("свои сессии — действие человека над собой")]
    public async Task<IReadOnlyList<SessionOut>> Sessions(CancellationToken cancellationToken)
    {
        var live = await tokens.ListAsync(actor.Id!.Value, TokenKinds.Session, cancellationToken);

        return [.. live.Select(row => new SessionOut(
            row.Id,
            row.Name,
            row.Address?.ToString(),
            row.CreatedAt,
            row.LastSeenAt,
            row.ExpiresAt,
            row.Id == actor.SessionId))];
    }

    /// <summary>Закрыть один из своих входов.</summary>
    [HttpDelete("sessions/{id:guid}")]
    [AuthenticatedEndpoint("закрыть свою сессию — действие человека над собой")]
    public async Task<IActionResult> Revoke(Guid id, CancellationToken cancellationToken)
    {
        var mine = await db.Set<AuthToken>()
            .AnyAsync(row => row.Id == id && row.UserId == actor.Id!.Value, cancellationToken);

        if (!mine)
        {
            // ⚠ «Не найдено», а не «не ваше».
            throw AppException.NotFound("Сессия не найдена");
        }

        await db.InTransactionAsync(
            (token) => revoker.RevokeAsync(id, RevokeReasons.Revoked, token),
            cancellationToken);

        return NoContent();
    }
}
