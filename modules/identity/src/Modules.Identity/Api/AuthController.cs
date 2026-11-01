using Modules.Core.Data;
using Modules.Core.Contracts.Tokens;
using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;
using Modules.Identity.Users;
using Modules.Core.Contracts.Authz;
using Modules.Core.Errors;
using Microsoft.AspNetCore.Mvc;

namespace Modules.Identity.Api;

/// <summary>Что присылают, входя.</summary>
internal sealed record LoginIn
{
    /// <summary>Печать без секретов: запись попадает в лог, в отладчик и в текст исключения.</summary>
    private bool PrintMembers(System.Text.StringBuilder builder)
    {
        builder.Append("секреты скрыты: Password, TrustedDeviceToken");

        return true;
    }

    [Required]
    [StringLength(320, MinimumLength = 3)]
    [JsonPropertyName("email")]
    public string Email { get; init; } = string.Empty;

    [Required]
    [StringLength(1024, MinimumLength = 1)]
    [JsonPropertyName("password")]
    public string Password { get; init; } = string.Empty;

    /// <summary>Класть ли токен обновления в куку.</summary>
    [JsonPropertyName("use_cookie")]
    public bool UseCookie { get; init; } = true;

    /// <summary>Доверие этой машине, выданное прошлым входом.</summary>
    [StringLength(200)]
    [JsonPropertyName("trusted_device_token")]
    public string? TrustedDeviceToken { get; init; }

    /// <summary>Запомнить эту машину — при входе паролем не действует.</summary>
    [JsonPropertyName("trust_device")]
    public bool TrustDevice { get; init; }
}

/// <summary>Что присылают, обновляя токен.</summary>
internal sealed record RefreshIn
{
    /// <summary>Печать без секретов: запись попадает в лог, в отладчик и в текст исключения.</summary>
    private bool PrintMembers(System.Text.StringBuilder builder)
    {
        builder.Append("секреты скрыты: RefreshToken");

        return true;
    }

    /// <summary>Токен обновления.</summary>
    [StringLength(200)]
    [JsonPropertyName("refresh_token")]
    public string? RefreshToken { get; init; }

    [JsonPropertyName("use_cookie")]
    public bool UseCookie { get; init; } = true;
}

/// <summary>Выданная пара.</summary>
internal sealed record TokensOut(
    [property: JsonPropertyName("access_token")] string AccessToken,
    [property: JsonPropertyName("access_expires_at")] DateTimeOffset AccessExpiresAt,
    [property: JsonPropertyName("refresh_token")] string? RefreshToken,
    [property: JsonPropertyName("refresh_expires_at")] DateTimeOffset RefreshExpiresAt,
    [property: JsonPropertyName("session_id")] Guid SessionId,
    [property: JsonPropertyName("trusted_device_token")] string? TrustedDeviceToken = null)
{
    /// <summary>Печать без секретов: запись попадает в лог, в отладчик и в текст исключения.</summary>
    private bool PrintMembers(System.Text.StringBuilder builder)
    {
        builder.Append("секреты скрыты: AccessToken, RefreshToken, TrustedDeviceToken");

        return true;
    }
}

/// <summary>Завершение входа кодом второго фактора.</summary>
internal sealed record TwoFactorIn
{
    /// <summary>Печать без секретов: запись попадает в лог, в отладчик и в текст исключения.</summary>
    private bool PrintMembers(System.Text.StringBuilder builder)
    {
        builder.Append("секреты скрыты: Code");

        return true;
    }

    [StringLength(32)]
    [JsonPropertyName("client_kind")]
    public string? ClientKind { get; init; }
    [StringLength(300)]
    [JsonPropertyName("client_name")]
    public string? ClientName { get; init; }
    /// <summary>Расписка, выданная на первом шаге.</summary>
    [Required]
    [StringLength(200)]
    [JsonPropertyName("challenge")]
    public string Challenge { get; init; } = string.Empty;

    /// <summary>Код из приложения или код из письма.</summary>
    [Required]
    [StringLength(64)]
    [JsonPropertyName("code")]
    public string Code { get; init; } = string.Empty;

    /// <summary>Класть ли токен обновления в куку.</summary>
    [JsonPropertyName("use_cookie")]
    public bool UseCookie { get; init; } = true;

    /// <summary>Запомнить эту машину: код только что введён, и это доказательство.</summary>
    [JsonPropertyName("trust_device")]
    public bool TrustDevice { get; init; }
}

/// <summary>Просьба выслать код письмом по уже выданной расписке.</summary>
internal sealed record TwoFactorEmailIn
{
    /// <summary>Расписка, выданная на первом шаге.</summary>
    [Required]
    [StringLength(200)]
    [JsonPropertyName("challenge")]
    public string Challenge { get; init; } = string.Empty;
}

/// <summary>Вход, обновление, выход и вызов второго фактора.</summary>
[ApiController]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
[Route("api/auth")]
internal sealed class AuthController(
    SignInService signIn,
    ITokenIssuer tokens,
    ITokenRevoker revoker,
    AppDbContext db,
    Users.PasswordPolicy policy,
    ICurrentUser actor,
    IdentityModuleOptions options) : ControllerBase
{
    /// <summary>Имя куки с токеном обновления.</summary>
    private string RefreshCookie => options.RefreshCookieName;

    /// <summary>Кука доверия браузеру.</summary>
    private string TrustCookie => options.TrustCookieName;

    /// <summary>Войти по адресу и паролю.</summary>
    [HttpPost("login")]
    [PublicEndpoint("вход: до него человек по определению никто")]
    public async Task<TokensOut> Login([FromBody] LoginIn body, CancellationToken cancellationToken)
    {
        // ⚠ ⚠ Тело важнее куки, а кука — запасной путь.
        var доверие = body.TrustedDeviceToken;

        if (string.IsNullOrEmpty(доверие) && Request.Cookies.TryGetValue(TrustCookie, out var изКуки))
        {
            доверие = изКуки;
        }

        var исход = await signIn.SignInAsync(
            body.Email, body.Password, Origin(), доверие, body.TrustDevice, cancellationToken);

        return Respond(исход, body.UseCookie);
    }

    /// <summary>Обменять токен обновления на новую пару.</summary>
    [HttpPost("refresh")]
    [PublicEndpoint("обновление: токен доступа к этому моменту уже истёк, пропуск — сам refresh")]
    public async Task<TokensOut> Refresh([FromBody] RefreshIn body, CancellationToken cancellationToken)
    {
        var token = body.RefreshToken;
        var кукой = false;

        if (string.IsNullOrEmpty(token) && Request.Cookies.TryGetValue(RefreshCookie, out var fromCookie))
        {
            token = fromCookie;
            кукой = true;
        }

        if (string.IsNullOrEmpty(token))
        {
            throw new AppException(
                ErrorCodes.SessionExpired, "Сессия кончилась. Войдите заново.", System.Net.HttpStatusCode.Unauthorized);
        }

        // ⚠ Оболочка остаётся здесь, работа уехала (открытый вопрос 2 плана 10.5).
        var pair = await tokens.RotateAsync(token, Origin(), cancellationToken);

        // ⚠ ⚠ Доверия здесь НЕТ и быть не может: обновление пары — это поворот токенов, а не вход.
        return Respond(new SignInOutcome(pair, null), body.UseCookie || (кукой && options.RefreshCookieStaysCookie));
    }

    /// <summary>Выйти: закрыть текущую сессию.</summary>
    [HttpPost("logout")]
    [AuthenticatedEndpoint("выход из своей сессии — действие человека над собой")]
    [AllowWhilePasswordChangeRequired("уйти можно всегда: запирать человека внутри — не защита")]
    [AllowWhileTwoFactorEnrollmentRequired("уйти можно всегда")]
    public async Task<IActionResult> Logout(CancellationToken cancellationToken)
    {
        if (actor.SessionId is { } id)
        {
            await db.InTransactionAsync(
                (token) => revoker.RevokeAsync(id, RevokeReasons.SignedOut, token),
                cancellationToken);
        }

        Response.Cookies.Delete(RefreshCookie, CookieOptions());

        // ⚠ ⚠ Куку доверия здесь НЕ снимаем, и это намеренно (решение 0063).

        return NoContent();
    }

    /// <summary>Завершить вход кодом второго фактора.</summary>
    [HttpPost("two-factor")]
    [PublicEndpoint("вход не завершён: пропуск — расписка, выданная в обмен на пароль")]
    public async Task<TokensOut> CompleteTwoFactor(
        [FromBody] TwoFactorIn body, CancellationToken cancellationToken)
    {
        if (body.ClientKind == "extension" && body.TrustDevice && !body.UseCookie)
            throw new AppException(ErrorCodes.Validation, "Расширение не хранит предъявительское доверие в JavaScript");
        var исход = await signIn.CompleteTwoFactorAsync(
            body.Challenge, body.Code, Origin(), body.TrustDevice, cancellationToken,
            body.ClientKind ?? (body.UseCookie ? "browser" : "desktop"), body.ClientName,
            Request.Cookies[TrustCookie]);

        // ⚠ ⚠ Признак берётся ИЗ ЗАПРОСА, а не зашивается.
        return Respond(исход, body.UseCookie);
    }

    /// <summary>Действующие требования к паролю.</summary>
    [HttpGet("password-policy")]
    [AuthenticatedEndpoint("требования к своему паролю — сведения о себе, а не о системе")]
    [AllowWhilePasswordChangeRequired("политика нужна до ввода нового пароля")]
    [AllowWhileTwoFactorEnrollmentRequired("смена пароля разрешена до настройки фактора")]
    public async Task<PasswordPolicyOut> PasswordPolicy(CancellationToken cancellationToken)
    {
        var правила = await policy.CurrentAsync(cancellationToken);

        return new PasswordPolicyOut(
            правила.MinLength,
            правила.MaxLengthBytes,
            правила.RequireUppercase,
            правила.RequireLowercase,
            правила.RequireDigit,
            правила.RequireSymbol);
    }

    /// <summary>Выслать код письмом — «нет доступа к коду?».</summary>
    [HttpPost("two-factor/email")]
    [PublicEndpoint("вход не завершён: пропуск — расписка, выданная в обмен на пароль")]
    public async Task<IActionResult> SendTwoFactorEmail(
        [FromBody] TwoFactorEmailIn body, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(body);

        await signIn.SendEmailCodeAsync(body.Challenge, cancellationToken);

        return NoContent();
    }

    private RequestOrigin Origin() => new(
        HttpContext.Connection.RemoteIpAddress,
        Request.Headers.UserAgent.FirstOrDefault() is { Length: > 0 } agent
            ? agent[..Math.Min(agent.Length, 512)]
            : null);

    /// <summary>Собрать ответ и, если просили, положить токен обновления в куку.</summary>
    private TokensOut Respond(SignInOutcome исход, bool useCookie)
    {
        var pair = исход.Pair;

        if (!useCookie)
        {
            return new TokensOut(
                pair.Access, pair.AccessExpiresAt, pair.Refresh, pair.RefreshExpiresAt, pair.TokenId,
                исход.TrustToken);
        }

        var options = CookieOptions();
        options.Expires = pair.RefreshExpiresAt;

        Response.Cookies.Append(RefreshCookie, pair.Refresh, options);

        // ⚠ ⚠ Доверие браузера — тоже кука, и телом оно НЕ дублируется.
        if (исход.TrustToken is { Length: > 0 } доверие)
        {
            var доверенная = CookieOptions();
            доверенная.Expires = исход.TrustExpiresAt;

            Response.Cookies.Append(TrustCookie, доверие, доверенная);
        }

        return new TokensOut(
            pair.Access, pair.AccessExpiresAt, null, pair.RefreshExpiresAt, pair.TokenId);
    }

    /// <summary>Признаки куки.</summary>
    private static Microsoft.AspNetCore.Http.CookieOptions CookieOptions() => new()
    {
        HttpOnly = true,
        Secure = true,
        SameSite = Microsoft.AspNetCore.Http.SameSiteMode.Strict,
        Path = "/",
        IsEssential = true,
    };
}
