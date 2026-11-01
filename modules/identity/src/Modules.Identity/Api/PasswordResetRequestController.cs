using Modules.Identity.Users;
using Modules.Core.Contracts.Authz;
using Modules.Core.Features;
using Microsoft.AspNetCore.Mvc;

namespace Modules.Identity.Api;

/// <summary>Самостоятельный сброс пароля: забывший просит ссылку письмом.</summary>
[ApiController]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
[Route("api/password-reset")]
[Feature("identity.self_service_reset")]
internal sealed class PasswordResetRequestController(UserService users) : ControllerBase
{
    /// <summary>Попросить ссылку на смену пароля.</summary>
    [HttpPost("request")]
    [PublicEndpoint("забывший пароль по определению не вошёл; ответ одинаков при любом исходе")]
    // ⚠ Не `Request`: у `ControllerBase` уже есть свойство с таким именем, и совпадение скрыло бы наследника.
    public async Task<IActionResult> Ask(
        [FromBody] ResetRequestIn body, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(body);

        await users.RequestPasswordResetAsync(body.Email, cancellationToken);

        return Accepted();
    }
}
