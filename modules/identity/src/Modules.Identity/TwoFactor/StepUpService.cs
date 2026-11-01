using System.Net;
using Modules.Identity.Storage;
using Modules.Identity.Users;
using Modules.Core.Contracts.Audit;
using Modules.Core.Contracts.Authz;
using Modules.Core.Contracts.Tokens;
using Modules.Core.Data;
using Modules.Core.Errors;
using Modules.Identity.Contracts;
using Modules.Identity.Domain;

namespace Modules.Identity.TwoFactor;

/// <summary>Переподтвердить личность на текущем токене.</summary>
internal sealed class StepUpService(
    TwoFactorService factors,
    LoginCodes codes,
    SecondFactorPolicy methods,
    IStepUpMarker marker,
    ICurrentUser current,
    IAuditWriter audit,
    AppDbContext db,
    UserRepository users,
    LoginFailures failures,
    TimeProvider clock,
    IdentityModuleOptions options,
    StepUpFreshness freshness)
{
    /// <summary>Проверить код и отметить токен.</summary>
    public async Task<DateTimeOffset> ConfirmAsync(string code, CancellationToken cancellationToken = default)
    {
        var кто = current.Id
            ?? throw new AppException(
                ErrorCodes.Validation, "Действие требует входа", HttpStatusCode.Unauthorized);

        // ⚠ Токен обязан быть назван.
        var токен = current.SessionId
            ?? throw new AppException(
                IdentityErrorCodes.TwoFactorInvalid,
                "Этим способом входа личность не переподтверждают.",
                HttpStatusCode.Forbidden);

        var сейчас = clock.GetUtcNow();

        var человек = await users.FindAsync(кто, cancellationToken)
            ?? throw new AppException(
                ErrorCodes.UserInactive, "Учётная запись отключена", HttpStatusCode.Unauthorized);

        var завёл = await factors.ConfirmedAsync(кто, cancellationToken);

        // ⚠ ⚠ Аутентификатор обязателен всем (решение 0120, IdentityModuleOptions.RequireAuthenticatorForEveryone).
        if (options.RequireAuthenticatorForEveryone && !завёл)
        {
            throw ПодключитеПриложение();
        }

        // ⚠ Запертая учётная запись переподтверждения не проходит.
        var попытка = await failures.TakeAsync(кто, сейчас, cancellationToken);

        if (попытка is null)
        {
            await ОтказатьAsync(кто, токен, "locked", cancellationToken);

            throw Отказ();
        }

        // ⚠ Код расходуется и без транзакции: принятый шаг записывается условным обновлением, которое фиксируется само.
        var принят = await factors.VerifyAsync(кто, code, CancellationToken.None)
            || (Письмом(завёл)
                && await codes.VerifyAsync(кто, code, CancellationToken.None, токен.ToString("N"), Purposes.StepUpCode));

        if (!принят)
        {
            await failures.RecordAsync(
                человек,
                попытка,
                "step_up_bad_code",
                cancellationToken,
                Строка(кто, токен, AuditResult.Deny, "invalid_code"));

            throw Отказ();
        }

        await failures.ReturnAsync(кто, попытка, cancellationToken);

        var помечен = await db.InTransactionAsync(
            async token =>
            {
                // ⚠ Отметка ставится ТОЛЬКО после принятого кода, и порядок здесь не косметика.
                if (!await marker.MarkAsync(токен, сейчас, token))
                {
                    return false;
                }

                audit.Write(Строка(кто, токен, AuditResult.Allow, null));
                var fresh = await users.FindAsync(кто, token);
                if (fresh is null || !fresh.IsActive || fresh.LockedUntil > сейчас) throw Отказ();
                fresh.TwoFactorConfiguredAt ??= сейчас;

                await db.SaveChangesAsync(token);

                return true;
            },
            cancellationToken);

        if (!помечен)
        {
            await ОтказатьAsync(кто, токен, "token_revoked", cancellationToken);

            throw Отказ();
        }

        return сейчас;
    }

    /// <summary>Записать отказ своим сохранением — до того, как вызывающий поднимет исключение.</summary>
    public async Task SendEmailAsync(CancellationToken cancellationToken = default)
    {
        var owner = current.Id ?? throw Отказ();
        var session = current.SessionId ?? throw Отказ();
        var user = await users.FindAsync(owner, cancellationToken) ?? throw Отказ();
        if (!user.IsActive || user.LockedUntil > clock.GetUtcNow()) throw Отказ();

        var завёл = await factors.ConfirmedAsync(user.Id, cancellationToken);

        if (options.RequireAuthenticatorForEveryone && !завёл)
        {
            throw ПодключитеПриложение();
        }

        if (!Письмом(завёл))
        {
            throw new AppException(
                IdentityErrorCodes.TwoFactorInvalid,
                "Подтверждение — кодом из приложения-аутентификатора; код письмом его не заменяет.",
                HttpStatusCode.Conflict);
        }

        if (!(await methods.AvailableAsync(user, cancellationToken)).Contains(SecondFactorMethods.Email))
        {
            throw new AppException(ErrorCodes.MailNotConfigured, "Почта недоступна. Используйте приложение.", HttpStatusCode.ServiceUnavailable);
        }
        await db.InTransactionAsync(async token =>
        {
            SignInService.EnsureMail(await codes.IssueAsync(user, token, session.ToString("N"), Purposes.StepUpCode));
            await db.SaveChangesAsync(token);
        }, cancellationToken);
    }

    /// <summary>Изменять защиту можно после недавнего кода; первичная ограниченная настройка остаётся доступной.</summary>
    public async Task RequireRecentAsync(bool allowInitialEnrollment = true, CancellationToken cancellationToken = default)
    {
        if (allowInitialEnrollment && current.MustEnrollTwoFactor)
        {
            return;
        }

        var (свежа, минут) = await freshness.CheckAsync(current.StepUpAt, cancellationToken);

        if (!свежа)
        {
            throw new AppException(ErrorCodes.SecondFactorRequired,
                "Подтвердите личность кодом второго фактора.", HttpStatusCode.Forbidden,
                new Dictionary<string, object?>(StringComparer.Ordinal) { ["window_minutes"] = минут });
        }
    }

    /// <summary>Принимает ли переподтверждение код письмом у этого человека (решение 0120).</summary>
    private bool Письмом(bool завёл) =>
        options.Features.StepUpEmail && !(options.AuthenticatorExcludesEmailCode && завёл);

    /// <summary>Отказ тому, у кого аутентификатора нет, когда он обязателен всем (решение 0120).</summary>
    private static AppException ПодключитеПриложение() => new(
        ErrorCodes.TwoFactorEnrollmentRequired,
        "Подключите приложение-аутентификатор: код письмом сессию не подтверждает.",
        HttpStatusCode.Forbidden);

    private async Task ОтказатьAsync(Guid кто, Guid токен, string причина, CancellationToken cancellationToken)
    {
        audit.Write(Строка(кто, токен, AuditResult.Deny, причина));

        await db.SaveChangesAsync(CancellationToken.None);
    }

    private static AuditEntry Строка(Guid кто, Guid токен, AuditResult исход, string? причина) =>
        new(
            IdentityActions.StepUp,
            ActorId: кто,
            Result: исход,
            ResourceType: "session",
            ResourceId: токен,
            Reason: причина);

    private static AppException Отказ() =>
        new(IdentityErrorCodes.TwoFactorInvalid, "Код не подошёл.", HttpStatusCode.Forbidden);
}
