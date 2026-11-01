using Modules.Identity.Settings;
using System.Net;
using Modules.Identity.Contracts;
using Modules.Identity.Domain;
using Modules.Identity.Naming;
using Modules.Identity.Storage;
using Modules.Identity.TwoFactor;
using Modules.Core.Contracts.Audit;
using Modules.Core.Contracts.Mail;
using Modules.Core.Contracts.Settings;
using Modules.Core.Contracts.Tokens;
using Modules.Crypto;
using Modules.Core.Data;
using Modules.Core.Errors;
using Microsoft.EntityFrameworkCore;
using Modules.Identity.Passwords;

namespace Modules.Identity.Users;

/// <summary>Чем кончился вход: пара токенов и, если человек попросил, доверие этой машине.</summary>
internal sealed record SignInOutcome(
    TokenPair Pair, string? TrustToken, DateTimeOffset? TrustExpiresAt = null)
{
    /// <summary>Печать без секретов: запись попадает в лог, в отладчик и в текст исключения.</summary>
    private bool PrintMembers(System.Text.StringBuilder builder)
    {
        builder.Append("секреты скрыты: TrustToken");

        return true;
    }
}

/// <summary>Вход: пароль, блокировка после неудач, второй фактор.</summary>
internal sealed class SignInService(
    Invitations invitations,
    TrustedClients clients,
    TwoFactorService factors,

    // ⚠ Способ фактора и код письмом (этап 17.7).
    SecondFactorPolicy methods,
    LoginCodes codes,
    AppDbContext db,
    UserRepository users,
    IPasswordHasher passwords,
    ITokenIssuer tokens,
    ISettingsReader settings,
    IAuditWriter audit,
    LoginFailures failures,
    SignInAttempts attempts,
    IIntegrityProtector integrity,
    TimeProvider clock,
    IdentityModuleOptions? options = null,

    // ⚠ Предел кодов на расписку (решение 0120).
    TwoFactor.ChallengeAttempts? challenges = null)
{
    /// <summary>Срок доверия клиенту, дней; ноль — доверие выключено.</summary>
    private async Task<int> ДнейДоверияAsync(CancellationToken cancellationToken) =>
        options?.Features.TrustedClients == false
            ? 0
            : await settings.GetAsync(IdentitySettings.TrustedDeviceDays, cancellationToken);

    /// <summary>Войти по паролю.</summary>
    public async Task<SignInOutcome> SignInAsync(
        string email,
        string password,
        RequestOrigin origin,
        string? trustedDeviceToken = null,
        bool rememberDevice = false,
        CancellationToken cancellationToken = default, string? clientKind = null)
    {
        ArgumentNullException.ThrowIfNull(email);
        ArgumentNullException.ThrowIfNull(origin);

        // ⚠ Предел на пару «адрес + почта» — ДО поиска и до Argon2 (решение 0096): отказ одинаков для любого адреса, и отбитая попытка не стоит хеша.
        attempts.Require(origin.Address, email);

        var now = clock.GetUtcNow();
        var user = await users.FindByEmailAsync(email.Trim(), cancellationToken);

        // ⚠ ⚠ Сверхдлинный пароль — одинаково для живого и выдуманного адреса (ревизия 2026-10-26, решение 0118).
        if (System.Text.Encoding.UTF8.GetByteCount(password) > PasswordHasher.MaxPasswordBytes)
        {
            await passwords.WasteVerificationTimeAsync(cancellationToken);
            await RecordFailureAsync(user?.Id, email, "password_too_long", cancellationToken);

            throw Invalid();
        }

        if (user is null)
        {
            // ⚠ Тратим столько же, сколько потратила бы настоящая проверка.
            await passwords.WasteVerificationTimeAsync(cancellationToken);
            await RecordFailureAsync(null, email, "unknown_email", cancellationToken);

            throw Invalid();
        }

        // ⚠ ⚠ Попытка берётся ДО Argon2 одним условным обновлением (решение 0110, разбор 2026-10-21).
        var попытка = await failures.TakeAsync(user.Id, now, cancellationToken);

        if (попытка is null)
        {
            await passwords.WasteVerificationTimeAsync(cancellationToken);
            await RecordFailureAsync(user.Id, email, "locked", cancellationToken);

            throw Invalid();
        }

        // ⚠ ⚠ Попытка взята и зафиксирована — дальше запрос не хозяин (ревизия 2026-10-26, решение 0118).
        if (!await failures.VerifyPasswordAsync(passwords, user.Id, попытка, user.PasswordHash, password))
        {
            await failures.NoticeAsync(user.Id, попытка, "bad_password", CancellationToken.None);
            await RecordFailureAsync(user.Id, email, "bad_password", CancellationToken.None);

            throw Invalid();
        }

        await failures.ReturnAsync(user.Id, попытка, CancellationToken.None);

        if (!user.IsActive)
        {
            await RecordFailureAsync(user.Id, email, "inactive", cancellationToken);

            throw Invalid();
        }

        // ⚠ ⚠ Пересчёт хеша — ДО транзакций (ревизия 2026-10-26, решение 0118).
        var перехеш = passwords.NeedsRehash(user.PasswordHash)
            ? await ПерехешAsync(password, cancellationToken)
            : null;

        // ⚠ Второй фактор — ПОСЛЕ пароля и активности, но ДО выдачи токенов.
        var способ = await methods.ResolveAsync(user, cancellationToken);

        if (user.IsSuperuser && user.TwoFactorConfiguredAt is null && способ == SecondFactorMethods.Email
            && await invitations.HasLiveAsync(user.Id, Purposes.SuperuserSetup, now, cancellationToken))
        {
            способ = SecondFactorMethods.None;
        }

        // ⚠ ⚠ ДОВЕРИЕ МАШИНЕ снимает второй фактор — и только его.
        var днейДоверия = await ДнейДоверияAsync(cancellationToken);

        // ⚠ ⚠ Аутентификатор обязателен всем (решение 0120).
        var безДоверия = await НастройкаОбязательнаAsync(user.Id, cancellationToken);

        var trustedRow = днейДоверия > 0 && trustedDeviceToken is { Length: > 0 } && !безДоверия
            ? await clients.FindAsync(trustedDeviceToken, user.Id, false, cancellationToken) : null;
        if (clientKind is not null && trustedRow?.ClientKind != clientKind) trustedRow = null;
        var verifiedAt = trustedRow is { UsedAt: null } && trustedRow.ExpiresAt > now ? (DateTimeOffset?)trustedRow.CreatedAt : null;
        var доверено = verifiedAt is not null;

        if (доверено)
        {
            способ = SecondFactorMethods.None;
        }

        if (способ == SecondFactorMethods.None && !доверено && user.TwoFactorConfiguredAt is not null)
        {
            throw new AppException(IdentityErrorCodes.TwoFactorUnavailable,
                "Второй фактор сейчас недоступен. Обратитесь к администратору.", HttpStatusCode.ServiceUnavailable);
        }

        if (способ != SecondFactorMethods.None)
        {
            var minutes = Math.Max(
                1, await settings.GetAsync(IdentitySettings.TwoFactorChallengeMinutes, cancellationToken));

            var challenge = await db.InTransactionAsync(
                async (token) =>
                {
                    var fresh = await users.FindAsync(user.Id, token) ?? throw Invalid();
                    EnsureCurrent(fresh, user.PasswordHash, now);
                    if (перехеш is not null && passwords.NeedsRehash(fresh.PasswordHash)) fresh.PasswordHash = перехеш;
                    var issued = await invitations.IssueAsync(
                        user.Id, Purposes.TwoFactor, TimeSpan.FromMinutes(minutes),
                        cancellationToken: token, binding: Invitations.CodeBinding(fresh.PasswordHash));

                    // ⚠ ⚠ Код письмом заводится и ставится в очередь В ТОЙ ЖЕ транзакции, что и расписка.
                    if (способ == SecondFactorMethods.Email)
                    {
                        EnsureMail(await codes.IssueAsync(user, token, Invitations.CodeBinding(issued.Token)));
                    }

                    await db.SaveChangesAsync(token);

                    return issued;
                },
                cancellationToken);

            throw new AppException(
                IdentityErrorCodes.TwoFactorRequired,
                "Введите код второго фактора",
                HttpStatusCode.Unauthorized,
                new Dictionary<string, object?>
                {
                    ["challenge"] = challenge.Token,
                    ["expires_at"] = challenge.ExpiresAt,
                    ["trust_days"] = днейДоверия,

                    // ⚠ Способ уезжает клиенту: экран должен сказать, откуда брать код — из приложения или из почты.
                    ["method"] = способ,

                    // ⚠ ⚠ Остальные доступные способы — ради «нет доступа к коду?» (решение 0061).
                    ["available"] = await methods.AvailableAsync(user, cancellationToken),
                });
        }

        return await db.InTransactionAsync(
            async (token) =>
            {
                // ⚠ Перечитываем внутри попытки.
                var fresh = await users.FindAsync(user.Id, token) ?? throw Invalid();
                EnsureCurrent(fresh, user.PasswordHash, now);

                fresh.FailedLoginCount = 0;
                fresh.LockedUntil = null;
                fresh.LockoutLevel = 0;
                fresh.UpdatedAt = now;

                // ⚠ Пересчёт хеша, если параметры argon2 усилили.
                if (перехеш is not null && passwords.NeedsRehash(fresh.PasswordHash))
                {
                    fresh.PasswordHash = перехеш;
                }

                // ⚠ Выдаёт СОСЕД, и внутри нашей транзакции.
                if (доверено)
                {
                    var checkedTrust = await clients.FindAsync(trustedDeviceToken!, fresh.Id, true, token);
                    if (checkedTrust is null || checkedTrust.UsedAt is not null || checkedTrust.ExpiresAt <= clock.GetUtcNow())
                        throw Expired(ErrorCodes.SessionExpired, "Доверие отозвано. Повторите вход.");
                    checkedTrust.LastSeenAt = now;
                    checkedTrust.Address = origin.Address;
                }
                var pair = await tokens.IssueAsync(new TokenIssueRequest(fresh.Id, TokenKinds.Session,
                    origin.UserAgent, origin.Address, DeviceRef: доверено ? TrustedClients.Ref(trustedRow!.Id) : null,
                    StepUpAt: verifiedAt), token);

                var доверие = await ДоверитьAsync(
                    fresh.Id, false, днейДоверия, token);

                audit.Write(new AuditEntry(
                    IdentityActions.LoggedIn,
                    ActorId: fresh.Id,
                    ResourceType: IdentityResources.Session,
                    ResourceId: pair.TokenId,
                    Changes: Отметки(доверено, доверие.Token is not null)));

                await db.SaveChangesAsync(token);

                return new SignInOutcome(pair, доверие.Token, доверие.ExpiresAt);
            },
            cancellationToken);
    }

    /// <summary>Выдать доверие этой машине, если человек попросил и установка это разрешает.</summary>
    private async Task<(string? Token, DateTimeOffset? ExpiresAt)> ДоверитьAsync(
        Guid userId, bool remember, int дней, CancellationToken cancellationToken)
    {
        if (!remember || дней <= 0)
        {
            return (null, null);
        }

        var выдано = await invitations.IssueAsync(
            userId,
            Purposes.TrustedDevice,
            TimeSpan.FromDays(дней),
            quenchPrevious: false,
            cancellationToken: cancellationToken);

        // ⚠ Срок отдаётся наружу ради КУКИ браузера.
        return (выдано.Token, выдано.ExpiresAt);
    }

    /// <summary>Что записать в журнал про второй фактор этого входа.</summary>
    private static Dictionary<string, object?> Отметки(bool доверено, bool выдано) =>
        new(StringComparer.Ordinal)
        {
            ["second_factor"] = доверено ? "trusted_device" : "none",
            ["trusted_device_issued"] = выдано,
        };

    /// <summary>Завершить вход: расписка плюс код.</summary>
    public async Task<SignInOutcome> CompleteTwoFactorAsync(
        string challenge,
        string code,
        RequestOrigin origin,
        bool rememberDevice = false,
        CancellationToken cancellationToken = default, string clientKind = "browser", string? clientName = null, string? existingTrust = null)
    {
        var receipt = (
            Owner: await invitations.PeekAsync(challenge, Purposes.TwoFactor, cancellationToken),
            Binding: await invitations.PeekBindingAsync(challenge, Purposes.TwoFactor, cancellationToken));

        if (receipt.Owner is not { } userId)
        {
            throw Expired(ErrorCodes.SessionExpired, "Вход не завершён вовремя. Начните заново.");
        }

        var user = await users.FindAsync(userId, cancellationToken);

        if (user is not { IsActive: true })
        {
            await RecordFailureAsync(userId, user?.Email ?? string.Empty, "inactive", cancellationToken);

            throw Invalid();
        }

        var now = clock.GetUtcNow();

        if (user.LockedUntil is { } until && until > now)
        {
            await RecordFailureAsync(userId, user.Email, "locked", cancellationToken);

            throw Invalid();
        }

        if (receipt.Binding != Invitations.CodeBinding(user.PasswordHash))
        {
            throw Expired(ErrorCodes.SessionExpired, "Пароль изменён. Начните вход заново.");
        }

        // ⚠ ⚠ Попытка расписки — до попытки учётной записи и до проверки кода (решение 0120, IdentityModuleOptions.ChallengeCodeAttempts).
        if (challenges is { } счёт && !счёт.Take(challenge))
        {
            await invitations.ConsumeAsync(challenge, Purposes.TwoFactor, CancellationToken.None);

            throw Expired(ErrorCodes.SessionExpired, "Слишком много неверных кодов. Начните вход заново.");
        }

        // ⚠ ⚠ Чем вход будет принят — параметры проекта (решение 0120).
        var (письмом, настройка) = await СпособыЗавершенияAsync(userId, cancellationToken);

        // ⚠ ⚠ Проверяются ОБА способа, и порядок здесь не важен.
        var (исход, попытка) = await db.InTransactionAsync(
            async token =>
            {
                var взята = await failures.TakeAsync(userId, now, token);

                if (взята is null)
                {
                    return (Попытка.Заперта, взята);
                }

                var подошёл = await factors.VerifyAsync(userId, code, token)
                    || (письмом && await codes.VerifyAsync(userId, code, token, Invitations.CodeBinding(challenge)));

                if (подошёл)
                {
                    await failures.ReturnAsync(userId, взята, token);
                }

                return (подошёл ? Попытка.Подошла : Попытка.Неверна, взята);
            },
            cancellationToken);

        if (исход == Попытка.Заперта)
        {
            await RecordFailureAsync(userId, user.Email, "locked", cancellationToken);

            throw Invalid();
        }

        if (исход == Попытка.Неверна)
        {
            await failures.NoticeAsync(userId, попытка!, "bad_code", CancellationToken.None);
            await RecordFailureAsync(userId, user.Email, "bad_code", CancellationToken.None);

            var исчерпана = challenges?.Exhausted(challenge) == true;

            if (исчерпана)
            {
                await invitations.ConsumeAsync(challenge, Purposes.TwoFactor, CancellationToken.None);
            }

            var retry = !исчерпана && await db.Set<User>().AsNoTracking()
                .AnyAsync(row => row.Id == userId && row.IsActive && (row.LockedUntil == null || row.LockedUntil <= now), cancellationToken);

            throw new AppException(
                IdentityErrorCodes.TwoFactorInvalid, "Код не подошёл", HttpStatusCode.Unauthorized,
                new Dictionary<string, object?> { ["can_retry"] = retry });
        }

        return await db.InTransactionAsync(
            async (token) =>
            {
                var fresh = await users.FindAsync(userId, token) ?? throw Invalid();
                EnsureCurrent(fresh, user.PasswordHash, now);
                if (await invitations.ConsumeAsync(challenge, Purposes.TwoFactor, token) != userId)
                    throw Expired(ErrorCodes.SessionExpired, "Вход уже завершён. Начните заново.");

                // ⚠ ⚠ Сессия настройки (решение 0120): письмо доказало ящик, но аутентификатор обязателен.
                if (!настройка)
                {
                    fresh.TwoFactorConfiguredAt ??= now;
                }

                fresh.FailedLoginCount = 0;
                fresh.LockedUntil = null;
                fresh.LockoutLevel = 0;
                fresh.UpdatedAt = now;

                // ⚠ Выдаёт СОСЕД, и внутри нашей транзакции.
                var days = await ДнейДоверияAsync(token);
                var clientGrant = rememberDevice && days > 0 && !настройка
                    ? await clients.IssueAsync(fresh.Id, clientKind, clientName, origin, days, existingTrust, token) : null;

                // ⚠ Отметка фактора — временем кода.
                var pair = await tokens.IssueAsync(new TokenIssueRequest(fresh.Id, TokenKinds.Session,
                    origin.UserAgent, origin.Address, DeviceRef: clientGrant is null ? null : TrustedClients.Ref(clientGrant.Id),
                    StepUpAt: настройка ? null : now), token);

                // ⚠ ⚠ Доверие выдаётся ЗДЕСЬ, а не на первом шаге входа: только сейчас человек доказал вторым фактором, что машина его.
                var доверие = (Token: clientGrant?.Token, ExpiresAt: clientGrant?.ExpiresAt);

                audit.Write(new AuditEntry(
                    IdentityActions.LoggedIn,
                    ActorId: fresh.Id,
                    ResourceType: IdentityResources.Session,
                    ResourceId: pair.TokenId,
                    Changes: Завершение(доверие.Token is not null, настройка)));

                await db.SaveChangesAsync(token);

                return new SignInOutcome(pair, доверие.Token, доверие.ExpiresAt);
            },
            cancellationToken);
    }

    /// <summary>Выслать код письмом по живой расписке — «нет доступа к коду?».</summary>
    public async Task SendEmailCodeAsync(string challenge, CancellationToken cancellationToken = default)
    {
        var userId = await invitations.PeekAsync(challenge, Purposes.TwoFactor, cancellationToken)
            ?? throw Invalid();

        var user = await users.FindAsync(userId, cancellationToken);

        if (user is not { IsActive: true })
        {
            throw Invalid();
        }

        if (user.LockedUntil is { } until && until > clock.GetUtcNow())
        {
            throw Invalid();
        }

        // ⚠ ⚠ Владельцу аутентификатора — отказ, и прямой (решение 0120, AuthenticatorExcludesEmailCode).
        if (options?.AuthenticatorExcludesEmailCode == true && await factors.ConfirmedAsync(user.Id, cancellationToken))
        {
            throw new AppException(
                IdentityErrorCodes.TwoFactorInvalid,
                "Вход подтверждается приложением-аутентификатором; код письмом его не заменяет. "
                + "Нет доступа к приложению — попросите администратора снять второй фактор.",
                HttpStatusCode.Conflict);
        }

        // ⚠ Почты нет — говорим прямо, а не молчим.
        if (!(await methods.AvailableAsync(user, cancellationToken)).Contains(SecondFactorMethods.Email))
        {
            throw new AppException(
                IdentityErrorCodes.TwoFactorInvalid,
                "Код письмом на этой установке недоступен",
                HttpStatusCode.Conflict);
        }

        await db.InTransactionAsync(
            async (token) =>
            {
                EnsureMail(await codes.IssueAsync(user, token, Invitations.CodeBinding(challenge)));

                await db.SaveChangesAsync(token);
            },
            cancellationToken);
    }

    /// <summary>Обязательна ли этому человеку настройка аутентификатора (решение 0120).</summary>
    private async Task<bool> НастройкаОбязательнаAsync(Guid userId, CancellationToken cancellationToken) =>
        options?.RequireAuthenticatorForEveryone == true && !await factors.ConfirmedAsync(userId, cancellationToken);

    /// <summary>Принимается ли код письмом и кончится ли вход сессией настройки (решение 0120).</summary>
    private async Task<(bool Письмом, bool Настройка)> СпособыЗавершенияAsync(Guid userId, CancellationToken cancellationToken)
    {
        if (options is not ({ AuthenticatorExcludesEmailCode: true } or { RequireAuthenticatorForEveryone: true }))
        {
            return (true, false);
        }

        var завёл = await factors.ConfirmedAsync(userId, cancellationToken);

        return (!(options.AuthenticatorExcludesEmailCode && завёл), options.RequireAuthenticatorForEveryone && !завёл);
    }

    /// <summary>Что записать в журнал про вход кодом.</summary>
    private static Dictionary<string, object?> Завершение(bool выдано, bool настройка)
    {
        var отметки = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["two_factor"] = true,
            ["trusted_device_issued"] = выдано,
        };

        if (настройка)
        {
            отметки["second_factor"] = "enrollment";
        }

        return отметки;
    }

    /// <summary>Пересчитать хеш усиленным профилем — если хешер не занят.</summary>
    private async Task<string?> ПерехешAsync(string password, CancellationToken cancellationToken)
    {
        try
        {
            return await passwords.HashAsync(password, cancellationToken);
        }
        catch (AppException занято) when (PasswordHasher.Busy(занято))
        {
            return null;
        }
    }

    private static void EnsureCurrent(User user, string passwordHash, DateTimeOffset now)
    {
        if (!user.IsActive || user.DeletedAt is not null || user.PasswordHash != passwordHash
            || user.LockedUntil > now)
        {
            throw Invalid();
        }
    }

    internal static void EnsureMail(MailQueueResult result)
    {
        if (result != MailQueueResult.Queued)
        {
            throw new AppException(result == MailQueueResult.RateLimited ? ErrorCodes.RateLimited : ErrorCodes.MailNotConfigured,
                result == MailQueueResult.RateLimited ? "Код уже запрошен. Подождите перед повторной отправкой."
                    : "Почта недоступна. Выберите приложение или обратитесь к администратору.",
                result == MailQueueResult.RateLimited ? HttpStatusCode.TooManyRequests : HttpStatusCode.ServiceUnavailable);
        }
    }

    private async Task RecordFailureAsync(
        Guid? userId, string email, string reason, CancellationToken cancellationToken)
    {
        // ⚠ В журнал — настоящая причина, наружу — одна на всех.
        audit.Write(new AuditEntry(
            IdentityActions.LoggedIn,
            ActorId: userId,
            Result: AuditResult.Deny,
            ResourceType: IdentityResources.Session,
            Reason: reason,

            // ⚠ ⚠ Адрес — ОТПЕЧАТКОМ, а не строкой (решение владельца 2026-10-21, решение 0110, находка 9).
            Changes: new Dictionary<string, object?>
            {
                ["email_fingerprint"] = integrity.Fingerprint(EmailFingerprint, email.Trim().ToLowerInvariant()),
            }));

        await db.SaveChangesAsync(CancellationToken.None);
    }

    /// <summary>Назначение отпечатка адреса неудачного входа.</summary>
    internal const string EmailFingerprint = "login-email";

    /// <summary>Один и тот же отказ на все причины — иначе форма входа становится оракулом.</summary>
    private static AppException Invalid() => new(
        IdentityErrorCodes.InvalidCredentials, "Неверный адрес или пароль", HttpStatusCode.Unauthorized);

    private static AppException Expired(string code, string message) =>
        new(code, message, HttpStatusCode.Unauthorized);

    /// <summary>Чем кончилась проверка кода второго фактора под замком строки.</summary>
    private enum Попытка
    {
        Подошла,
        Неверна,
        Заперта,
    }

}
