using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;
using Modules.Identity.Storage;
using Modules.Identity.TwoFactor;
using Modules.Identity.Users;
using Modules.Core.Contracts.Authz;
using Modules.Core.Errors;
using Microsoft.AspNetCore.Mvc;
using Modules.Identity.Contracts;

namespace Modules.Identity.Api;

/// <summary>Пароль — переспрашивается там, где угнанная вкладка иначе сняла бы защиту.</summary>
internal sealed record TwoFactorConfirmIn
{
    /// <summary>Печать без секретов: запись попадает в лог, в отладчик и в текст исключения.</summary>
    private bool PrintMembers(System.Text.StringBuilder builder)
    {
        builder.Append("секреты скрыты: Code");

        return true;
    }

    /// <summary>Код из приложения.</summary>
    [Required]
    [StringLength(64)]
    [JsonPropertyName("code")]
    public string Code { get; init; } = string.Empty;
}

/// <summary>Действие, требующее подтвердить, кто просит.</summary>
internal sealed record PasswordIn
{
    /// <summary>Печать без секретов: запись попадает в лог, в отладчик и в текст исключения.</summary>
    private bool PrintMembers(System.Text.StringBuilder builder)
    {
        builder.Append("секреты скрыты: Password");

        return true;
    }

    /// <summary>Текущий пароль.</summary>
    [Required]
    [StringLength(1024)]
    [JsonPropertyName("password")]
    public string Password { get; init; } = string.Empty;
}

/// <summary>Что показать человеку, включающему фактор.</summary>
internal sealed record TwoFactorSetupOut(
    [property: JsonPropertyName("secret")] string Secret,
    [property: JsonPropertyName("uri")] string Uri)
{
    /// <summary>Печать без секретов: запись попадает в лог, в отладчик и в текст исключения.</summary>
    private bool PrintMembers(System.Text.StringBuilder builder)
    {
        builder.Append("секреты скрыты: Secret");

        return true;
    }
}

/// <summary>Состояние второго фактора.</summary>
internal sealed record TwoFactorStatusOut(
    [property: JsonPropertyName("enrolled")] bool Enrolled,
    [property: JsonPropertyName("confirmed")] bool Confirmed,
    [property: JsonPropertyName("required")] bool Required,
    [property: JsonPropertyName("chosen_method")] string? ChosenMethod = null,
    [property: JsonPropertyName("effective_method")] string? EffectiveMethod = null,
    [property: JsonPropertyName("available_methods")] IReadOnlyList<string>? AvailableMethods = null);

/// <summary>Каким быть своему второму фактору.</summary>
internal sealed record TwoFactorMethodIn(
    [property: JsonPropertyName("method")] string? Method);

/// <summary>Смена своего пароля.</summary>
internal sealed record ChangePasswordIn
{
    /// <summary>Печать без секретов: запись попадает в лог, в отладчик и в текст исключения.</summary>
    private bool PrintMembers(System.Text.StringBuilder builder)
    {
        builder.Append("секреты скрыты: CurrentPassword, NewPassword");

        return true;
    }

    /// <summary>Текущий пароль.</summary>
    [Required]
    [StringLength(1024, MinimumLength = 1)]
    [JsonPropertyName("current_password")]
    public string CurrentPassword { get; init; } = string.Empty;

    /// <summary>Новый.</summary>
    [Required]
    [StringLength(1024, MinimumLength = 1)]
    [JsonPropertyName("new_password")]
    public string NewPassword { get; init; } = string.Empty;
}

/// <summary>Код для переподтверждения личности.</summary>
internal sealed record StepUpIn
{
    /// <summary>Печать без секретов: запись попадает в лог, в отладчик и в текст исключения.</summary>
    private bool PrintMembers(System.Text.StringBuilder builder)
    {
        builder.Append("секреты скрыты: Code");

        return true;
    }

    /// <summary>Код из приложения или код восстановления.</summary>
    [Required]
    [StringLength(64, MinimumLength = 4)]
    public string Code { get; init; } = string.Empty;
}

/// <summary>Когда личность подтвердили и до какого момента это открывает действия под свежим фактором.</summary>
internal sealed record StepUpOut(
    [property: JsonPropertyName("confirmed_at")] DateTimeOffset ConfirmedAt,
    [property: JsonPropertyName("step_up_until")] DateTimeOffset? StepUpUntil = null);

/// <summary>Что человек может — карта для интерфейса.</summary>
internal sealed record EffectiveOut(
    [property: JsonPropertyName("permissions")] IReadOnlyList<string> Permissions);

/// <summary>Что человек меняет себе сам.</summary>
internal sealed record ProfileIn
{
    /// <summary>Отображаемое имя.</summary>
    [StringLength(200)]
    [JsonPropertyName("display_name")]
    public string? DisplayName { get; init; }
}

/// <summary>Кто я.</summary>
internal sealed record MeOut(
    [property: JsonPropertyName("id")] Guid Id,
    [property: JsonPropertyName("email")] string Email,
    [property: JsonPropertyName("display_name")] string? DisplayName,
    [property: JsonPropertyName("is_superuser")] bool IsSuperuser,
    [property: JsonPropertyName("must_change_password")] bool MustChangePassword,
    [property: JsonPropertyName("session_id")] Guid? SessionId,
    [property: JsonPropertyName("must_enroll_two_factor")] bool MustEnrollTwoFactor = false,
    [property: JsonPropertyName("step_up_until")] DateTimeOffset? StepUpUntil = null);
/// <summary>Всё своё: профиль, права, пароль, второй фактор, сессии.</summary>
[ApiController]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
[Route("api/me")]
internal sealed class MeController(
    UserService people,
    TwoFactorService factors,
    // ⚠ КОНТРАКТ, а не движок.
    IPermissionChecker authz,
    UserRepository users,
    StepUpService stepUp,
    SecondFactorPolicy methods,
    ICurrentUser actor,
    StepUpFreshness freshness,
    IdentityModuleOptions options) : ControllerBase
{
    /// <summary>Кто я.</summary>
    [HttpGet]
    [AuthenticatedEndpoint("интерфейс не знает даже, чьё имя показывать, пока не спросит")]
    [AllowApiToken("чтение о себе безвредно: скрипт узнаёт, чьим ключом он ходит")]
    [AllowWhilePasswordChangeRequired("экрану смены пароля надо знать, кто на нём стоит")]
    [AllowWhileTwoFactorEnrollmentRequired("экрану включения фактора — тоже")]
    public async Task<MeOut> Me(CancellationToken cancellationToken)
    {
        // ⚠ ЧТЕНИЕ, а не `FindAsync`: тот берёт сущность целиком и с трекингом — это путь записи.
        var user = await users.ReadAsync(actor.Id!.Value, cancellationToken)
            ?? throw AppException.NotFound("Пользователь не найден");

        // ⚠ Окно — у того же, кто отказывает (решение 0120): экран не обещает минут, которых у сервера нет.
        return new MeOut(
            user.Id, user.Email, user.DisplayName, user.IsSuperuser, user.MustChangePassword, actor.SessionId, actor.MustEnrollTwoFactor,
            await freshness.UntilAsync(actor.StepUpAt, cancellationToken));
    }

    /// <summary>Изменить своё имя.</summary>
    [HttpPatch]
    [AuthenticatedEndpoint("своё имя — действие человека над собой")]
    public async Task<MeOut> UpdateMe(
        [FromBody] ProfileIn body, CancellationToken cancellationToken)
    {
        var user = await people.UpdateOwnProfileAsync(body.DisplayName, cancellationToken);

        return new MeOut(
            user.Id, user.Email, user.DisplayName, user.IsSuperuser, user.MustChangePassword, actor.SessionId, actor.MustEnrollTwoFactor,
            await freshness.UntilAsync(actor.StepUpAt, cancellationToken));
    }

    /// <summary>Состояние второго фактора.</summary>
    [HttpGet("two-factor")]
    [AuthenticatedEndpoint("свой второй фактор — действие человека над собой")]
    [AllowWhilePasswordChangeRequired("доступные способы нужны для подтверждения смены пароля")]
    [AllowWhileTwoFactorEnrollmentRequired("экрану включения надо знать, с чего начинать")]
    public async Task<TwoFactorStatusOut> TwoFactorStatus(CancellationToken cancellationToken)
    {
        var status = await factors.StatusAsync(cancellationToken);
        var (выбран, действует) = await people.SecondFactorAsync(cancellationToken);

        return new TwoFactorStatusOut(
            status.Enrolled, status.Confirmed, status.Required, выбран, действует,
            await methods.AvailableAsync((await users.FindAsync(actor.Id!.Value, cancellationToken))!, cancellationToken));
    }

    /// <summary>Выбрать себе способ второго фактора.</summary>
    [HttpPut("two-factor")]
    [AuthenticatedEndpoint("свой второй фактор — действие человека над собой")]
    [AllowWhileTwoFactorEnrollmentRequired("выбор способа — часть включения фактора")]
    public async Task<TwoFactorStatusOut> ChooseTwoFactor(
        [FromBody] TwoFactorMethodIn body, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(body);

        var owner = await users.FindAsync(actor.Id!.Value, cancellationToken);
        await stepUp.RequireRecentAsync(await ПервичнаяНастройкаAsync(owner, cancellationToken), cancellationToken);
        await people.ChooseSecondFactorAsync(body.Method, cancellationToken);

        return await TwoFactorStatus(cancellationToken);
    }

    /// <summary>Завести секрет и показать его.</summary>
    [HttpPost("two-factor/totp/setup")]
    [AuthenticatedEndpoint("свой второй фактор — действие человека над собой")]
    [AllowWhileTwoFactorEnrollmentRequired("ради этого действия ограничение и заведено")]
    public async Task<TwoFactorSetupOut> SetupTwoFactor(CancellationToken cancellationToken)
    {
        var owner = await users.FindAsync(actor.Id!.Value, cancellationToken);
        await stepUp.RequireRecentAsync(await ПервичнаяНастройкаAsync(owner, cancellationToken), cancellationToken);
        var setup = await factors.SetupAsync(cancellationToken);

        return new TwoFactorSetupOut(setup.Secret, setup.Uri);
    }

    /// <summary>Подтвердить фактор кодом.</summary>
    [HttpPost("two-factor/totp/confirm")]
    [AuthenticatedEndpoint("свой второй фактор — действие человека над собой")]
    [AllowWhileTwoFactorEnrollmentRequired("ради этого действия ограничение и заведено")]
    public async Task<IActionResult> ConfirmTwoFactor(
        [FromBody] TwoFactorConfirmIn body, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(body);
        var owner = await users.FindAsync(actor.Id!.Value, cancellationToken);
        await stepUp.RequireRecentAsync(await ПервичнаяНастройкаAsync(owner, cancellationToken), cancellationToken);

        await factors.ConfirmAsync(body.Code, cancellationToken);

        return NoContent();
    }

    /// <summary>Отключить фактор.</summary>
    [HttpPost("two-factor/totp/disable")]
    [AuthenticatedEndpoint("свой второй фактор — действие человека над собой")]
    public async Task<IActionResult> DisableTwoFactor(
        [FromBody] PasswordIn body, CancellationToken cancellationToken)
    {
        await stepUp.RequireRecentAsync(cancellationToken: cancellationToken);

        // ⚠ После отключения у человека останется письмо — если почта есть.
        if (!await methods.MailAsync(cancellationToken))
            throw new AppException(IdentityErrorCodes.TwoFactorNotEnrolled,
                "Приложение — единственный доступный второй фактор. Сначала настройте отправку кодов по почте.",
                System.Net.HttpStatusCode.Conflict);
        await factors.DisableAsync(body.Password, cancellationToken);

        return NoContent();
    }

    /// <summary>Переподтвердить личность на этой сессии.</summary>
    [HttpPost("step-up")]
    [AuthenticatedEndpoint("подтверждение своей личности — действие человека над собой")]
    [AllowWhilePasswordChangeRequired("свежий фактор требуется перед обязательной сменой пароля")]
    [AllowWhileTwoFactorEnrollmentRequired("код завершает обязательную настройку этой сессии")]
    public async Task<StepUpOut> StepUp(
        [FromBody] StepUpIn body, CancellationToken cancellationToken)
    {
        var когда = await stepUp.ConfirmAsync(body.Code, cancellationToken);

        // ⚠ Конец окна — у того же, кто его назначает (решение 0120).
        return new StepUpOut(когда, await freshness.UntilAsync(когда, cancellationToken));
    }

    // ⚠ «Прислать письмом код подтверждения» (POST /api/me/step-up/email).

    /// <summary>Что мне можно — глобально или на конкретном ресурсе.</summary>
    [HttpGet("permissions")]
    [AuthenticatedEndpoint("свои права — действие человека над собой")]
    [AllowApiToken("скрипту надо знать, что ему открыто, — иначе он узнаёт это отказами")]
    public async Task<EffectiveOut> Effective(
        [FromQuery(Name = "resource_type")] string? resourceType,
        [FromQuery(Name = "resource_id")] Guid? resourceId,
        CancellationToken cancellationToken)
    {
        if (actor.Id is not { } id)
        {
            return new EffectiveOut([]);
        }

        return new EffectiveOut(await authz.EffectiveAsync(id, resourceType, resourceId, cancellationToken));
    }

    /// <summary>Сменить себе пароль.</summary>
    [HttpPost("password")]
    [AuthenticatedEndpoint("смена своего пароля — действие человека над собой")]
    [AllowWhilePasswordChangeRequired("ради этого действия ограничение и заведено")]
    [AllowWhileTwoFactorEnrollmentRequired("сменить пароль можно и не включив ещё фактор")]
    public async Task<IActionResult> ChangePassword(
        [FromBody] ChangePasswordIn body, CancellationToken cancellationToken)
    {
        var owner = await users.FindAsync(actor.Id!.Value, cancellationToken);
        await stepUp.RequireRecentAsync(await ПервичнаяНастройкаAsync(owner, cancellationToken), cancellationToken);
        await people.ChangePasswordAsync(body.CurrentPassword, body.NewPassword, cancellationToken);

        return NoContent();
    }

    /// <summary>Открыта ли этой учётной записи первичная настройка без свежего фактора.</summary>
    private async Task<bool> ПервичнаяНастройкаAsync(Domain.User? owner, CancellationToken cancellationToken) =>
        owner?.TwoFactorConfiguredAt is null
        || (options.RequireAuthenticatorForEveryone && !await factors.ConfirmedAsync(owner.Id, cancellationToken));

}
