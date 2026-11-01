using Modules.Identity.TwoFactor;
using Modules.Core.Contracts.Authz;
using Microsoft.AspNetCore.Mvc;

namespace Modules.Identity.Api;

/// <summary>Письмо с кодом переподтверждения личности для текущей сессии.</summary>
[ApiController]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
[Route("api/me/step-up/email")]
[Modules.Core.Features.Feature("identity.step_up_email")]
internal sealed class StepUpEmailController(StepUpService stepUp) : ControllerBase
{
    /// <summary>Прислать письмом код подтверждения личности для текущей сессии.</summary>
    [HttpPost]
    [AuthenticatedEndpoint("письмо подтверждения своей личности, привязанное к текущей сессии")]
    [AllowWhilePasswordChangeRequired("письмо позволяет подтвердить обязательную смену пароля")]
    [AllowWhileTwoFactorEnrollmentRequired("код завершает обязательную настройку этой сессии")]
    public async Task<IActionResult> SendStepUpEmail(CancellationToken cancellationToken)
    {
        await stepUp.SendEmailAsync(cancellationToken);

        return NoContent();
    }
}
