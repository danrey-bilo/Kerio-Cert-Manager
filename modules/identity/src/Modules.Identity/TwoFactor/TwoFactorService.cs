using Modules.Core.Contracts.Tokens;
using System.Net;
using System.Security.Cryptography;
using Modules.Identity.Contracts;
using Modules.Identity.Domain;
using Modules.Identity.Naming;
using Modules.Identity.Settings;
using Modules.Identity.Storage;
using Modules.Identity.Users;
using Modules.Core.Contracts.Audit;
using Modules.Core.Contracts.Authz;
using Modules.Core.Contracts.Settings;
using Modules.Crypto;
using Modules.Core.Data;
using Modules.Core.Errors;
using Microsoft.EntityFrameworkCore;
using Modules.Identity.Passwords;

namespace Modules.Identity.TwoFactor;

/// <summary>Что показать человеку, который включает фактор.</summary>
internal sealed record TwoFactorSetup(string Secret, string Uri)
{
    /// <summary>Печать без секретов: запись попадает в лог, в отладчик и в текст исключения.</summary>
    private bool PrintMembers(System.Text.StringBuilder builder)
    {
        builder.Append("секреты скрыты: Secret");

        return true;
    }
}

/// <summary>Состояние второго фактора у человека.</summary>
internal sealed record TwoFactorStatus(bool Enrolled, bool Confirmed, bool Required);

/// <summary>Второй фактор: включение, подтверждение, проверка, отключение.</summary>
internal sealed class TwoFactorService(
    AppDbContext db,
    UserRepository users,
    Invitations invitations,
    ITokenRevoker revoker,
    IStepUpMarker marker,
    LoginFailures failures,
    ISecretEnvelope envelope,
    IPasswordHasher passwords,
    ISettingsReader settings,
    IAuditWriter audit,
    ICurrentUser actor,
    IdentityModuleOptions options,
    TimeProvider clock)
{
    /// <summary>Привязка секрета к месту.</summary>
    private static string Context(Guid userId) => $"user:{userId}:totp_seed";

    /// <summary>Завести секрет и показать его человеку.</summary>
    public async Task<TwoFactorSetup> SetupAsync(CancellationToken cancellationToken = default)
    {
        var user = await RequireActorAsync(cancellationToken);
        var existing = await FindAsync(user.Id, cancellationToken);

        if (existing is { ConfirmedAt: not null })
        {
            throw new AppException(
                IdentityErrorCodes.TwoFactorAlreadyEnabled,
                "Второй фактор уже включён. Чтобы завести новый секрет, сначала отключите его.",
                HttpStatusCode.Conflict);
        }

        var secret = Totp.NewSecret();

        try
        {
            var key = envelope.CreateDataKey();
            var sealedSecret = envelope.Seal(key, secret, Context(user.Id));

            if (existing is null)
            {
                db.Add(new UserTwoFactor
                {
                    UserId = user.Id,
                    DataKey = key.Bytes,
                    Secret = sealedSecret.Bytes,
                    CreatedAt = clock.GetUtcNow(),
                });
            }
            else
            {
                existing.DataKey = key.Bytes;
                existing.Secret = sealedSecret.Bytes;
                existing.CreatedAt = clock.GetUtcNow();
                existing.LastStep = -1;
            }

            await db.SaveChangesAsync(cancellationToken);

            return new TwoFactorSetup(Totp.ToBase32(secret), Totp.Uri(options.TotpIssuer, user.Email, secret));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(secret);
        }
    }

    /// <summary>Подтвердить фактор кодом.</summary>
    public async Task ConfirmAsync(string code, CancellationToken cancellationToken = default)
    {
        await db.InTransactionAsync(
            async (token) =>
            {
                var user = await RequireActorAsync(token);

                var factor = await FindAsync(user.Id, token)
                    ?? throw new AppException(
                        IdentityErrorCodes.TwoFactorNotEnrolled,
                        "Секрет не заведён: начните с включения",
                        HttpStatusCode.Conflict);

                if (factor.ConfirmedAt is not null)
                {
                    throw new AppException(
                        IdentityErrorCodes.TwoFactorAlreadyEnabled, "Второй фактор уже включён", HttpStatusCode.Conflict);
                }

                var step = VerifyCode(factor, code)
                    ?? throw new AppException(
                        IdentityErrorCodes.TwoFactorInvalid, "Код не подошёл", HttpStatusCode.BadRequest);

                factor.ConfirmedAt = clock.GetUtcNow();
                factor.LastStep = step;
                user.TwoFactorConfiguredAt ??= factor.ConfirmedAt;
                if (actor.SessionId is { } session && !await marker.MarkAsync(session, factor.ConfirmedAt.Value, token))
                {
                    throw new AppException(ErrorCodes.SessionRevoked, "Сессия закрыта. Войдите заново.", HttpStatusCode.Unauthorized);
                }

                // ⚠ ⚠ ДОВЕРИЕ МАШИНАМ ГАСНЕТ (ревью 2026-09-15, S2, trust-1).
                await invitations.QuenchAsync(user.Id, Purposes.TrustedDevice, factor.ConfirmedAt.Value, token);

                await invitations.QuenchAsync(user.Id, Purposes.SuperuserSetup, factor.ConfirmedAt.Value, token);

                audit.Write(new AuditEntry(
                    IdentityActions.TwoFactorEnabled,
                    ActorId: user.Id,
                    ResourceType: IdentityResources.User,
                    ResourceId: user.Id));

                await db.SaveChangesAsync(token);
            },
            cancellationToken);
    }

    /// <summary>Отключить фактор.</summary>
    public async Task DisableAsync(string password, CancellationToken cancellationToken = default)
    {
        await EnsurePasswordAsync(await RequireActorAsync(cancellationToken), password, cancellationToken);

        await db.InTransactionAsync(
            async (token) =>
            {
                var user = await RequireActorAsync(token);

                var factor = await FindAsync(user.Id, token);

                if (factor is null)
                {
                    return;
                }

                db.Remove(factor);
                await invitations.QuenchAsync(user.Id, Purposes.TrustedDevice, clock.GetUtcNow(), token);
                await invitations.QuenchAsync(user.Id, Purposes.TwoFactor, clock.GetUtcNow(), token);

                audit.Write(new AuditEntry(
                    IdentityActions.TwoFactorDisabled,
                    ActorId: user.Id,
                    ResourceType: IdentityResources.User,
                    ResourceId: user.Id));

                await db.SaveChangesAsync(token);
            },
            cancellationToken);
    }

    /// <summary>Снять фактор <b>другому человеку</b>.</summary>
    public Task<bool> ResetForAsync(Guid userId, CancellationToken cancellationToken = default) =>
        db.InTransactionAsync(
            async (token) =>
            {
                var user = await users.FindAsync(userId, token)
                    ?? throw AppException.NotFound("Пользователь не найден");

                // ⚠ Фактор суперпользователя снимает только суперпользователь — разбор и цена у UserService.EnsureMayManage (ревью 2026-09-15, S2).
                UserService.EnsureMayManage(actor, user);

                return await ResetCoreAsync(user, fromConsole: false, token);
            },
            cancellationToken);

    /// <summary>Сбросить фактор суперпользователя из консоли с обязательным отзывом старого доступа (0084).</summary>
    internal Task<bool> ResetSuperuserForConsoleAsync(Guid userId, CancellationToken cancellationToken) =>
        db.InTransactionAsync(async token =>
        {
            var user = await users.FindAsync(userId, token)
                ?? throw AppException.NotFound("Суперпользователь не найден");
            UserService.EnsureSuperuserRecoveryTarget(user);
            return await ResetCoreAsync(user, fromConsole: true, token);
        }, cancellationToken);

    /// <summary>Снять фактор любой живой учётной записи из консоли — половина восстановления доступа (решение 0120).</summary>
    internal Task<bool> ResetForRecoveryAsync(Guid userId, CancellationToken cancellationToken) =>
        db.InTransactionAsync(async token =>
        {
            var user = await users.FindAsync(userId, token)
                ?? throw AppException.NotFound("Учётная запись не найдена");

            return await ResetCoreAsync(user, fromConsole: true, token, setupWindow: user.IsSuperuser);
        }, cancellationToken);

    private async Task<bool> ResetCoreAsync(User user, bool fromConsole, CancellationToken cancellationToken, bool setupWindow = true)
    {
        var factor = await FindAsync(user.Id, cancellationToken);
        var hadFactor = factor is not null || user.TwoFactorConfiguredAt is not null;
        if (!hadFactor && !fromConsole) return false;

        if (factor is not null) db.Remove(factor);
        var now = clock.GetUtcNow();
        user.TwoFactorConfiguredAt = null;
        user.UpdatedAt = now;
        if (fromConsole)
        {
            user.FailedLoginCount = 0;
            user.LockedUntil = null;
            user.LockoutLevel = 0;
        }
        await invitations.QuenchAsync(user.Id, Purposes.TrustedDevice, now, cancellationToken);
        await invitations.QuenchAsync(user.Id, Purposes.TwoFactor, now, cancellationToken);
        await invitations.QuenchAsync(user.Id, Purposes.LoginCode, now, cancellationToken);
        await invitations.QuenchAsync(user.Id, Purposes.StepUpCode, now, cancellationToken);

        if (fromConsole && setupWindow)
        {
            await invitations.IssueAsync(
                user.Id, Purposes.SuperuserSetup, options.SuperuserSetupWindow, cancellationToken: cancellationToken);
        }

        audit.Write(new AuditEntry(
            IdentityActions.TwoFactorReset,
            ActorId: fromConsole ? null : actor.Id,
            ActorType: fromConsole ? AuditActorType.System : null,
            ResourceType: IdentityResources.User,
            ResourceId: user.Id,
            Changes: fromConsole ? new Dictionary<string, object?> { ["via_cli"] = true } : null));
        await db.SaveChangesAsync(cancellationToken);
        // ⚠ Сброс фактора — восстановление: гаснут и ключи интеграции (решение 0110).
        await revoker.RevokeAllAsync(user.Id, RevokeReasons.TwoFactorReset, TokenKindMask.Everything, cancellationToken);
        return hadFactor;
    }

    /// <summary>Что показать на экране настроек.</summary>
    public async Task<TwoFactorStatus> StatusAsync(CancellationToken cancellationToken = default)
    {
        var user = await RequireActorAsync(cancellationToken);
        var factor = await FindAsync(user.Id, cancellationToken);

        return new TwoFactorStatus(
            factor is not null,
            factor is { ConfirmedAt: not null },
            true);
    }

    /// <summary>Проверить код аутентификатора при входе.</summary>
    public async Task<bool> VerifyAsync(Guid userId, string code, CancellationToken cancellationToken = default)
    {
        var factor = await FindAsync(userId, cancellationToken);

        if (factor is not { ConfirmedAt: not null })
        {
            return false;
        }

        if (VerifyCode(factor, code) is { } step)
        {
            // ⚠ Условным обновлением, а не присваиванием.
            var accepted = await db.Set<UserTwoFactor>()
                .Where(row => row.UserId == userId && row.LastStep < step)
                .ExecuteUpdateAsync(set => set.SetProperty(row => row.LastStep, step), cancellationToken);

            return accepted == 1;
        }

        return false;
    }

    /// <summary>Есть ли у человека действующий фактор.</summary>
    public Task<bool> ConfirmedAsync(Guid userId, CancellationToken cancellationToken = default) =>
        db.Set<UserTwoFactor>().AnyAsync(row => row.UserId == userId && row.ConfirmedAt != null, cancellationToken);

    /// <summary>Требует ли система фактор от этого человека.</summary>
    public Task<bool> RequiredAsync(User user, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(user);

        return RequiredAsync(user.IsSuperuser, cancellationToken);
    }

    /// <summary>То же по одному флагу — для путей, где строка человека не читается целиком.</summary>
    public async Task<bool> RequiredAsync(bool isSuperuser, CancellationToken cancellationToken = default) =>
        await settings.GetAsync(IdentitySettings.TotpRequired, cancellationToken)
        || (isSuperuser && await settings.GetAsync(IdentitySettings.TotpRequiredForSuperusers, cancellationToken));

    /// <summary>Развернуть секрет и проверить код.</summary>
    private long? VerifyCode(UserTwoFactor factor, string code)
    {
        var secret = envelope.Open(
            new WrappedDataKey(factor.DataKey),
            new SealedSecret(factor.Secret),
            Context(factor.UserId));

        try
        {
            return Totp.Verify(secret, code, clock.GetUtcNow(), factor.LastStep);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(secret);
        }
    }

    private Task<UserTwoFactor?> FindAsync(Guid userId, CancellationToken cancellationToken) =>
        db.Set<UserTwoFactor>().FirstOrDefaultAsync(row => row.UserId == userId, cancellationToken);

    private async Task<User> RequireActorAsync(CancellationToken cancellationToken)
    {
        if (actor.Id is not { } id)
        {
            throw new AppException(
                ErrorCodes.Unauthenticated, "Требуется вход", HttpStatusCode.Unauthorized);
        }

        return await users.FindAsync(id, cancellationToken)
            ?? throw new AppException(
                ErrorCodes.UserInactive, "Учётная запись отключена", HttpStatusCode.Unauthorized);
    }

    /// <summary>Переспросить пароль.</summary>
    private async Task EnsurePasswordAsync(User user, string password, CancellationToken cancellationToken)
    {
        // ⚠ Попытка берётся ДО Argon2 (решение 0110): параллельный подбор упирается в порог.
        var попытка = await failures.TakeAsync(user.Id, clock.GetUtcNow(), cancellationToken)
            ?? throw new AppException(
                IdentityErrorCodes.InvalidCredentials, "Пароль неверен", HttpStatusCode.BadRequest);

        if (await failures.VerifyPasswordAsync(passwords, user.Id, попытка, user.PasswordHash, password))
        {
            await failures.ReturnAsync(user.Id, попытка, CancellationToken.None);

            return;
        }

        await failures.RecordAsync(user, попытка, "reauth_bad_password", cancellationToken);

        throw new AppException(
            IdentityErrorCodes.InvalidCredentials, "Пароль неверен", HttpStatusCode.BadRequest);
    }
}
