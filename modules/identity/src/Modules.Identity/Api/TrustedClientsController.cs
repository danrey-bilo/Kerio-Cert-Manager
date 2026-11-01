using System.ComponentModel.DataAnnotations;
using System.Net;
using System.Text.Json.Serialization;
using Modules.Identity.Contracts;
using Modules.Identity.Domain;
using Modules.Identity.Settings;
using Modules.Identity.Users;
using Modules.Core.Contracts.Authz;
using Modules.Core.Contracts.Settings;
using Modules.Core.Contracts.Tokens;
using Modules.Core.Data;
using Modules.Core.Errors;
using Microsoft.AspNetCore.Mvc;

namespace Modules.Identity.Api;

internal sealed record TrustedClientOut(
    [property: JsonPropertyName("id")] Guid Id,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("client_kind")] string ClientKind,
    [property: JsonPropertyName("created_at")] DateTimeOffset CreatedAt,
    [property: JsonPropertyName("last_seen_at")] DateTimeOffset? LastSeenAt,
    [property: JsonPropertyName("expires_at")] DateTimeOffset ExpiresAt,
    [property: JsonPropertyName("revoked_at")] DateTimeOffset? RevokedAt,
    [property: JsonPropertyName("address")] string? Address,
    [property: JsonPropertyName("user_agent")] string? UserAgent,
    [property: JsonPropertyName("device_id")] Guid? DeviceId,
    [property: JsonPropertyName("is_current")] bool IsCurrent);

internal sealed record TrustedClientIn([property: JsonPropertyName("name")][StringLength(300)] string? Name);

/// <summary>Своё доверие входа: просмотр, выдача куки после свежей 2FA и отзыв конкретного клиента.</summary>
[ApiController]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
[Route("api/me/trusted-clients")]
[Modules.Core.Features.Feature("identity.trusted_clients")]
internal sealed class TrustedClientsController(TrustedClients clients, ICurrentUser actor, ISettingsReader settings, AppDbContext db, ISessionClientBinder binder, IdentityModuleOptions options) : ControllerBase
{
    private Guid UserId => actor.Id ?? throw AppException.NotFound("Пользователь не найден");

    /// <summary>Свои доверенные клиенты входа: браузеры, расширения и устройства.</summary>
    [HttpGet]
    [AuthenticatedEndpoint("свои доверенные клиенты")]
    public async Task<IReadOnlyList<TrustedClientOut>> List(CancellationToken cancellationToken)
    {
        var cookieId = Request.Cookies.TryGetValue(options.TrustCookieName, out var cookie)
            ? (await clients.FindAsync(cookie, UserId, false, cancellationToken))?.Id : null;
        var currentId = cookieId ?? TrustedClients.Id(actor.ClientRef);
        return [.. (await clients.ListAsync(UserId, cancellationToken)).Select(row => Out(row, currentId))];
    }

    /// <summary>Доверить этот клиент после свежего второго фактора: следующий вход не спросит код.</summary>
    [HttpPost]
    [RequireRecentSecondFactor]
    [AuthenticatedEndpoint("доверить свой браузер после подтверждения личности")]
    public async Task<IActionResult> Trust([FromBody] TrustedClientIn body, CancellationToken cancellationToken)
    {
        if (actor.Kind != TokenKinds.Session) throw new AppException(ErrorCodes.PermissionDenied, "Требуется сессия пользователя", HttpStatusCode.Forbidden);
        var days = await settings.GetAsync(IdentitySettings.TrustedDeviceDays, cancellationToken);
        if (days <= 0) throw new AppException(ErrorCodes.PermissionDenied, "Доверие выключено политикой", HttpStatusCode.Forbidden);
        var grant = await GrantAsync(body.Name, days, cancellationToken);
        Response.Cookies.Append(options.TrustCookieName, grant.Token, new Microsoft.AspNetCore.Http.CookieOptions
        {
            HttpOnly = true,
            Secure = true,
            SameSite = Microsoft.AspNetCore.Http.SameSiteMode.Strict,
            Path = "/",
            Expires = grant.ExpiresAt,
        });
        return NoContent();
    }

    /// <summary>Переименовать доверенный клиент.</summary>
    [HttpPatch("{id:guid}")]
    [RequireRecentSecondFactor]
    [AuthenticatedEndpoint("переименовать свой клиент")]
    public async Task<IActionResult> Rename(Guid id, [FromBody] TrustedClientIn body, CancellationToken cancellationToken)
    {
        await clients.RenameAsync(UserId, id, body.Name ?? string.Empty, cancellationToken);
        return NoContent();
    }

    /// <summary>Отозвать доверие клиенту: следующий вход с него снова спросит второй фактор.</summary>
    [HttpDelete("{id:guid}")]
    [RequireRecentSecondFactor]
    [AuthenticatedEndpoint("отозвать свой клиент и его связанные входы")]
    public async Task<IActionResult> Revoke(Guid id, CancellationToken cancellationToken)
    {
        await clients.RevokeAsync(UserId, id, cancellationToken);
        return NoContent();
    }

    private static TrustedClientOut Out(IdentityToken row, Guid? currentId) => new(row.Id, row.ClientName ?? "Browser",
        row.ClientKind ?? "legacy", row.CreatedAt, row.LastSeenAt, row.ExpiresAt, row.UsedAt,
        row.Address?.ToString(), row.UserAgent, row.DeviceId, row.Id == currentId);

    /// <summary>Выдача cookie и привязка живой сессии сохраняются одним действием.</summary>
    private Task<TrustedGrant> GrantAsync(string? name, int days, CancellationToken cancellationToken) => db.InTransactionAsync(async token =>
    {
        var issued = await clients.IssueAsync(UserId, "browser", name,
            new RequestOrigin(HttpContext.Connection.RemoteIpAddress, Request.Headers.UserAgent.ToString()), days,
            Request.Cookies[options.TrustCookieName], token, keepSession: actor.SessionId);
        await binder.BindAsync(UserId, actor.SessionId ?? throw new AppException(ErrorCodes.SessionExpired, "Сессия завершена"), TrustedClients.Ref(issued.Id), token);
        await db.SaveChangesAsync(token);
        return issued;
    }, cancellationToken);
}
