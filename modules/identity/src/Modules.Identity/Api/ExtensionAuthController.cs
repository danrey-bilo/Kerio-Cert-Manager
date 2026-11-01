using System.Net;
using Modules.Identity.Users;
using Modules.Core.Contracts.Authz;
using Modules.Core.Contracts.Tokens;
using Modules.Core.Data;
using Modules.Core.Errors;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Modules.Identity.Api;

/// <summary>Вход собственного расширения Chrome, с отдельными HttpOnly cookies и обычной 2FA.</summary>
[ApiController]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
[Route("api/auth/extension")]
[Modules.Core.Features.Feature("identity.extension_sign_in")]
internal sealed class ExtensionAuthController(SignInService signIn, ITokenIssuer tokens,
    ITokenRevoker revoker, AppDbContext db, ICurrentUser actor, IdentityModuleOptions options) : ControllerBase
{
    private string RefreshCookie => options.ExtensionRefreshCookieName;
    private string TrustCookie => options.ExtensionTrustCookieName;

    /// <summary>Вход расширения по паролю: при обязательном втором факторе отвечает вызовом, токены — после кода.</summary>
    [HttpPost("login")]
    [PublicEndpoint("расширение входит обычным паролем и вторым фактором")]
    public async Task<TokensOut> Login([FromBody] LoginIn body, CancellationToken token)
    {
        RequireExtension();
        var result = await signIn.SignInAsync(body.Email, body.Password, Origin(),
            Request.Cookies[TrustCookie], false, token, clientKind: "extension");
        return Respond(result);
    }

    /// <summary>Завершить вход расширения кодом второго фактора; обновление и доверие уходят HttpOnly-cookies.</summary>
    [HttpPost("two-factor")]
    [PublicEndpoint("завершение входа собственной 2FA, без внешних провайдеров")]
    public async Task<TokensOut> TwoFactor([FromBody] TwoFactorIn body, CancellationToken token)
    {
        RequireExtension();
        return Respond(await signIn.CompleteTwoFactorAsync(body.Challenge, body.Code, Origin(),
            body.TrustDevice, token, "extension", options.ExtensionClientName, Request.Cookies[TrustCookie]));
    }

    /// <summary>Прислать код входа расширения письмом по выданному вызову.</summary>
    [HttpPost("two-factor/email")]
    [PublicEndpoint("письмо по уже проверенному паролю и живой расписке")]
    public async Task<IActionResult> Email([FromBody] TwoFactorEmailIn body, CancellationToken token)
    {
        RequireExtension();
        await signIn.SendEmailCodeAsync(body.Challenge, token);
        return NoContent();
    }

    /// <summary>Обновить токен доступа расширения по HttpOnly-cookie обновления.</summary>
    [HttpPost("refresh")]
    [PublicEndpoint("пропуск — отдельная HttpOnly cookie расширения")]
    public async Task<TokensOut> Refresh(CancellationToken token)
    {
        RequireExtension();
        var refresh = Request.Cookies[RefreshCookie];
        if (string.IsNullOrEmpty(refresh)) throw new AppException(ErrorCodes.SessionExpired,
            "Войдите в расширение", HttpStatusCode.Unauthorized);
        return Respond(new SignInOutcome(await tokens.RotateAsync(refresh, Origin(), token), null));
    }

    /// <summary>Выйти из расширения: сессия гасится, cookies обновления и доверия снимаются.</summary>
    [HttpPost("logout")]
    [AuthenticatedEndpoint("закрытие только текущей сессии расширения")]
    [AllowWhilePasswordChangeRequired("выход разрешён всегда")]
    [AllowWhileTwoFactorEnrollmentRequired("выход разрешён всегда")]
    public async Task<IActionResult> Logout(CancellationToken token)
    {
        RequireExtension();
        if (actor.SessionId is { } id)
            await db.InTransactionAsync(ct => revoker.RevokeAsync(id, RevokeReasons.SignedOut, ct), token);
        Response.Cookies.Delete(RefreshCookie, Options());
        return NoContent();
    }

    private void RequireExtension()
    {
        if (options.ExtensionId is not { Length: > 0 } расширение || Request.Headers.Origin != "chrome-extension://" + расширение)
            throw AppException.PermissionDenied();
    }

    private RequestOrigin Origin() => new(HttpContext.Connection.RemoteIpAddress,
        options.ExtensionClientName + " / " + (Request.Headers.UserAgent.ToString() is { } value ? value[..Math.Min(value.Length, 480)] : ""));

    private TokensOut Respond(SignInOutcome outcome)
    {
        var refresh = Options();
        refresh.Expires = outcome.Pair.RefreshExpiresAt;
        Response.Cookies.Append(RefreshCookie, outcome.Pair.Refresh, refresh);
        if (outcome.TrustToken is { Length: > 0 } trust)
        {
            var options = Options();
            options.Expires = outcome.TrustExpiresAt;
            Response.Cookies.Append(TrustCookie, trust, options);
        }
        var pair = outcome.Pair;
        return new TokensOut(pair.Access, pair.AccessExpiresAt, null, pair.RefreshExpiresAt, pair.TokenId);
    }

    private static CookieOptions Options() => new()
    {
        HttpOnly = true,
        Secure = true,
        SameSite = SameSiteMode.Strict,
        Path = "/",
        IsEssential = true,
    };
}
