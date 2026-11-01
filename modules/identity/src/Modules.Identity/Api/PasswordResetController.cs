using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;
using Modules.Identity.Users;
using Modules.Core.Contracts.Authz;
using Modules.Core.Errors;
using Microsoft.AspNetCore.Mvc;

namespace Modules.Identity.Api;

/// <summary>Задать новый пароль по ссылке сброса.</summary>
internal sealed record ResetPasswordIn
{
    /// <summary>Печать без секретов: запись попадает в лог, в отладчик и в текст исключения.</summary>
    private bool PrintMembers(System.Text.StringBuilder builder)
    {
        builder.Append("секреты скрыты: Token, Password");

        return true;
    }

    /// <summary>Ссылка, выданная администратором.</summary>
    [Required]
    [StringLength(200)]
    [JsonPropertyName("token")]
    public string Token { get; init; } = string.Empty;

    /// <summary>Новый пароль.</summary>
    [Required]
    [StringLength(1024, MinimumLength = 1)]
    [JsonPropertyName("password")]
    public string Password { get; init; } = string.Empty;
}

/// <summary>Кто просит ссылку на смену пароля.</summary>
internal sealed record ResetRequestIn
{
    /// <summary>Адрес, на который слать ссылку.</summary>
    [Required]
    [StringLength(320)]
    [JsonPropertyName("email")]
    public string Email { get; init; } = string.Empty;
}

/// <summary>Ссылка сброса в теле запроса: почтовый секрет не попадает в URL API.</summary>
internal sealed record ResetLinkIn
{
    /// <summary>Печать без секретов: запись попадает в лог, в отладчик и в текст исключения.</summary>
    private bool PrintMembers(System.Text.StringBuilder builder)
    {
        builder.Append("секреты скрыты: Token");

        return true;
    }

    [Required]
    [StringLength(200)]
    [JsonPropertyName("token")]
    public string Token { get; init; } = string.Empty;
}

/// <summary>Сброс пароля по ссылке.</summary>
[ApiController]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
[Route("api/password-reset")]
internal sealed class PasswordResetController(UserService users, PasswordPolicy policy) : ControllerBase
{
    /// <summary>До ввода пароля проверить ссылку и показать действующие требования.</summary>
    [HttpPost("info")]
    [PublicEndpoint("владелец ссылки ещё не вошёл; пропуском служит действующий одноразовый токен")]
    public async Task<PasswordPolicyOut> Info([FromBody] ResetLinkIn body, CancellationToken cancellationToken)
    {
        if (!await users.CanResetPasswordAsync(body.Token, cancellationToken))
            throw AppException.NotFound("Ссылка недействительна");
        var rules = await policy.CurrentAsync(cancellationToken);
        return new PasswordPolicyOut(rules.MinLength, rules.MaxLengthBytes, rules.RequireUppercase,
            rules.RequireLowercase, rules.RequireDigit, rules.RequireSymbol);
    }

    /// <summary>Задать новый пароль.</summary>
    [HttpPost]
    [PublicEndpoint("сбрасывающий пароль по определению не вошёл: пропуск — сама ссылка")]
    public async Task<UserOut> Reset(
        [FromBody] ResetPasswordIn body, CancellationToken cancellationToken) =>
        UserOut.From(await users.ResetPasswordAsync(body.Token, body.Password, cancellationToken));
}
