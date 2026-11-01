using Modules.Identity.Users;
using Modules.Core.Contracts.Authz;
using Modules.Core.Errors;
using Microsoft.AspNetCore.Mvc;

namespace Modules.Identity.Api;

/// <summary>Активация по приглашению.</summary>
[ApiController]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
[Route("api/activation")]
internal sealed class ActivationController(UserService users, Users.PasswordPolicy policy) : ControllerBase
{
    /// <summary>Кого приглашает эта ссылка.</summary>
    [HttpGet]
    [PublicEndpoint("активирующий ещё не может войти: пароля у него нет, пропуск — сама ссылка")]
    public async Task<ActionResult<ActivationInfoOut>> Info(
        [FromQuery(Name = "token")] string token, CancellationToken cancellationToken) =>
        await InfoCoreAsync(token, cancellationToken);

    /// <summary>Кого приглашает эта ссылка — токен в теле запроса.</summary>
    [HttpPost("info")]
    [PublicEndpoint("активирующий ещё не может войти: пароля у него нет, пропуск — сама ссылка")]
    public async Task<ActionResult<ActivationInfoOut>> InfoByBody(
        [FromBody] ActivationLinkIn body, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(body);

        return await InfoCoreAsync(body.Token, cancellationToken);
    }

    /// <summary>Общая часть обоих путей: один ответ и один отказ.</summary>
    private async Task<ActivationInfoOut> InfoCoreAsync(string token, CancellationToken cancellationToken)
    {
        var user = await users.PeekActivationAsync(token, cancellationToken);

        if (user is null)
        {
            throw AppException.NotFound("Ссылка недействительна");
        }

        // ⚠ ⚠ Требования к паролю едут ВМЕСТЕ с проверкой ссылки (этап 37.1).
        var правила = await policy.CurrentAsync(cancellationToken);

        return new ActivationInfoOut(
            user.Email,
            user.DisplayName,
            new PasswordPolicyOut(
                правила.MinLength,
                правила.MaxLengthBytes,
                правила.RequireUppercase,
                правила.RequireLowercase,
                правила.RequireDigit,
                правила.RequireSymbol));
    }

    /// <summary>Задать пароль и активировать учётную запись.</summary>
    [HttpPost]
    [PublicEndpoint("завершение приглашения: единственный способ у человека получить доступ")]
    public async Task<UserOut> Activate(
        [FromBody] ActivateIn body, CancellationToken cancellationToken) =>
        UserOut.From(await users.ActivateAsync(body.Token, body.Password, cancellationToken));
}
