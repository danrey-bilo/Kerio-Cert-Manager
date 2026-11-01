using Modules.Core.Contracts.Mail;
using Modules.Core.Contracts.Accounts;
using System.ComponentModel.DataAnnotations;
using Modules.Core.Contracts.Tokens;
using System.Net;
using Modules.Identity.Contracts;
using Modules.Identity.Domain;
using Modules.Identity.Mail;
using Modules.Identity.TwoFactor;
using Modules.Identity.Naming;
using Modules.Identity.Notifications;
using Modules.Identity.Storage;
using Modules.Core.Contracts.Audit;
using Modules.Core.Contracts.Authz;
using Modules.Core.Data;
using Modules.Core.Errors;
using Microsoft.EntityFrameworkCore;
using Modules.Identity.Passwords;

namespace Modules.Identity.Users;

/// <summary>Что задают при заведении учётной записи.</summary>
internal sealed record UserDraft(
    string Email,
    string? DisplayName = null,
    string? Password = null,
    bool IsSuperuser = false,
    bool MustChangePassword = true,
    string? Department = null)
{
    /// <summary>Печать без секретов: запись попадает в лог, в отладчик и в текст исключения.</summary>
    private bool PrintMembers(System.Text.StringBuilder builder)
    {
        builder.Append("секреты скрыты: Password");

        return true;
    }
}

/// <summary>Что можно изменить у заведённой учётной записи.</summary>
internal sealed record UserPatch(
    string? DisplayName = null,
    bool? IsSuperuser = null,
    bool? MustChangePassword = null,
    string? Department = null);

/// <summary>Жизненный цикл учётной записи.</summary>
internal sealed class UserService(
    AppDbContext db,
    UserRepository users,
    Invitations invitations,

    ITokenRevoker tokens,
    PasswordPolicy policy,
    IPasswordHasher passwords,
    IAuditWriter audit,
    SecurityNotices notices,

    // ⚠ Доступ человека — роли, членство, выданное лично — у модуля доступа (решение 0108).
    IAccountAccess accountAccess,

    // ⚠ Письма ставятся В ТЕХ ЖЕ транзакциях, что и сами действия (этап 17.6).
    MailNotices mail,

    // ⚠ Разрешение способа второго фактора — соседняя служба того же модуля.
    SecondFactorPolicy methods,

    // ⚠ Счётчик неудач внутри открытой сессии — тот же, что у входа (ревью 2026-09-15, S2): текущий пароль при смене перебирался без счёта.
    LoginFailures failures,
    ICurrentUser actor,
    IdentityModuleOptions options,
    TimeProvider clock)
{
    /// <summary>Тип ресурса в журнале.</summary>

    /// <summary>Выполнить изменение одной транзакцией.</summary>

    /// <summary>Завести учётную запись.</summary>
    public Task<(User User, Invitation? Invitation)> CreateAsync(
        UserDraft draft, CancellationToken cancellationToken = default) =>
        db.InTransactionAsync(token => CreateCoreAsync(draft, token), cancellationToken);

    private async Task<(User User, Invitation? Invitation)> CreateCoreAsync(
        UserDraft draft, CancellationToken cancellationToken)
    {
        var email = NormalizeForCreation(draft.Email);

        if (draft.Password is { } password)
        {
            await policy.EnsureAsync(password, cancellationToken);
        }

        if (await users.EmailTakenAsync(email, cancellationToken))
        {
            throw new AppException(
                IdentityErrorCodes.UserAlreadyExists, "Такой адрес уже занят", HttpStatusCode.Conflict);
        }

        // ⚠ Флаг наибольших прав выдаёт только тот, у кого он есть.
        if (draft.IsSuperuser)
        {
            EnsureMaySetSuperuser(requested: true);
        }

        var now = clock.GetUtcNow();

        var user = new User
        {
            Id = Guid.CreateVersion7(),
            Email = email,
            DisplayName = draft.DisplayName,
            IsSuperuser = draft.IsSuperuser,
            Department = Отдел(draft.Department),
            CreatedAt = now,
            UpdatedAt = now,
        };

        Invitation? invitation = null;

        if (draft.Password is { } given)
        {
            user.PasswordHash = await passwords.HashAsync(given, cancellationToken);
            user.PasswordChangedAt = now;
            user.MustChangePassword = draft.MustChangePassword;

            user.ActivatedAt = now;
        }
        else
        {
            user.PasswordHash = Unusable;

            user.MustChangePassword = false;
        }

        users.Add(user);

        if (draft.Password is null)
        {
            invitation = await invitations.IssueAsync(user.Id, Purposes.Activation, cancellationToken: cancellationToken);
        }

        audit.Write(new AuditEntry(
            IdentityActions.UserCreated,
            ActorId: actor.Id,
            ResourceType: IdentityResources.User,
            ResourceId: user.Id,
            Changes: new Dictionary<string, object?>
            {
                ["email"] = email,
                ["display_name"] = draft.DisplayName,
                ["department"] = user.Department,
                ["is_superuser"] = draft.IsSuperuser,
                ["by_invitation"] = draft.Password is null,
            }));

        if (invitation is not null)
        {
            audit.Write(new AuditEntry(
                IdentityActions.InvitationSent,
                ActorId: actor.Id,
                ResourceType: IdentityResources.User,
                ResourceId: user.Id,
                Changes: new Dictionary<string, object?> { ["expires_at"] = invitation.ExpiresAt }));
        }

        // ⚠ Постановка письма ДО сохранения и в той же единице работы: строка очереди уезжает в базу вместе с самим человеком.
        var письмо = MailQueueResult.NotConfigured;

        if (invitation is not null)
        {
            письмо = await mail.InvitationAsync(
                user, invitation.Token, invitation.ExpiresAt, cancellationToken);
        }

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException error) when (IsEmailConflict(error))
        {
            throw new AppException(
                IdentityErrorCodes.UserAlreadyExists, "Такой адрес уже занят", HttpStatusCode.Conflict);
        }

        // ⚠ ⚠ Токен возвращается ТОЛЬКО если почты нет.
        return (user, письмо == MailQueueResult.NotConfigured ? invitation : null);
    }

    /// <summary>Это нарушение уникальности адреса?</summary>
    private static string? Отдел(string? значение) =>
        string.IsNullOrWhiteSpace(значение) ? null : значение.Trim();

    private static bool IsEmailConflict(DbUpdateException error) =>
        error.InnerException?.Message.Contains(IdentityEntities.EmailIndex, StringComparison.Ordinal) == true;

    /// <summary>Изменить профиль и флаги.</summary>
    public async Task<User> UpdateAsync(
        Guid id, UserPatch patch, CancellationToken cancellationToken = default)
    {
        return await db.InTransactionAsync(async token => await UpdateCoreAsync(id, patch, token), cancellationToken);
    }

    private async Task<User> UpdateCoreAsync(Guid id, UserPatch patch, CancellationToken cancellationToken)
    {
        var user = await RequireAsync(id, cancellationToken);
        var changes = new Dictionary<string, object?>();
        EnsureMayManage(actor, user);

        if (patch.IsSuperuser is { } superuser && superuser != user.IsSuperuser)
        {
            EnsureMaySetSuperuser(superuser);

            if (!superuser)
            {
                await EnsureNotLastSuperuserAsync(user, cancellationToken);
            }

            user.IsSuperuser = superuser;
            changes["is_superuser"] = superuser;
        }

        if (patch.DisplayName is { } name && name != user.DisplayName)
        {
            user.DisplayName = name;
            changes["display_name"] = name;
        }

        if (patch.MustChangePassword is { } must && must != user.MustChangePassword)
        {
            user.MustChangePassword = must;
            changes["must_change_password"] = must;
        }

        if (patch.Department is { } отдел && Отдел(отдел) != user.Department)
        {
            user.Department = Отдел(отдел);
            changes["department"] = user.Department;
        }

        if (changes.Count == 0)
        {
            return user;
        }

        user.UpdatedAt = clock.GetUtcNow();

        audit.Write(new AuditEntry(
            IdentityActions.UserUpdated,
            ActorId: actor.Id,
            ResourceType: IdentityResources.User,
            ResourceId: user.Id,
            Changes: changes));

        await db.SaveChangesAsync(cancellationToken);

        return user;
    }

    /// <summary>Сменить себе пароль.</summary>
    public async Task ChangePasswordAsync(
        string current, string next, CancellationToken cancellationToken = default)
    {
        if (actor.Id is not { } id)
        {
            throw new AppException(
                ErrorCodes.Unauthenticated, "Требуется вход", HttpStatusCode.Unauthorized);
        }

        var user = await RequireAsync(id, cancellationToken);
        var now = clock.GetUtcNow();

        // ⚠ Запертая учётная запись смену не проходит, и счётчик не двигается.
        var попытка = await failures.TakeAsync(id, now, cancellationToken) ?? throw WrongCurrentPassword();

        if (!await failures.VerifyPasswordAsync(passwords, id, попытка, user.PasswordHash, current))
        {
            await failures.RecordAsync(user, попытка, "change_password_bad_password", cancellationToken);

            throw WrongCurrentPassword();
        }

        await failures.ReturnAsync(id, попытка, cancellationToken);

        var проверенный = user.PasswordHash;

        if (string.Equals(current, next, StringComparison.Ordinal))
        {
            throw new AppException(
                ErrorCodes.PasswordPolicyViolation,
                "Новый пароль совпадает с текущим",
                HttpStatusCode.UnprocessableEntity);
        }

        await policy.EnsureAsync(next, cancellationToken);

        // ⚠ Хеш — до транзакции (ревизия 2026-10-26, решение 0118): внутри неё Argon2 держал соединение пула, пока стоял в очереди хешера.
        var хеш = await passwords.HashAsync(next, cancellationToken);

        await db.InTransactionAsync(
            token => ChangePasswordCoreAsync(id, проверенный, хеш, token), cancellationToken);
    }

    private static AppException WrongCurrentPassword() =>
        new(IdentityErrorCodes.InvalidCredentials, "Текущий пароль неверен", HttpStatusCode.BadRequest);

    private async Task<bool> ChangePasswordCoreAsync(
        Guid id, string проверенный, string хеш, CancellationToken cancellationToken)
    {
        var user = await RequireAsync(id, cancellationToken);

        if (!string.Equals(user.PasswordHash, проверенный, StringComparison.Ordinal))
        {
            throw WrongCurrentPassword();
        }

        // ⚠ Доверие машинам гасит `ApplyNewPasswordAsync` — там же, где расписку второго фактора, и по той же причине (решение 0063).
        await ApplyNewPasswordAsync(user, хеш, byReset: false, cancellationToken);

        if (actor.SessionId is { } session)
        {
            await tokens.RevokeOthersAsync(
                user.Id, session, RevokeReasons.PasswordChanged, TokenKindMask.PasswordChange, cancellationToken);
        }
        else
        {
            await tokens.RevokeAllAsync(
                user.Id, RevokeReasons.PasswordChanged, TokenKindMask.PasswordChange, cancellationToken);
        }

        await db.SaveChangesAsync(cancellationToken);

        return true;
    }

    /// <summary>Выдать ссылку сброса пароля.</summary>
    public async Task<Invitation> IssuePasswordResetAsync(
        Guid id, CancellationToken cancellationToken = default)
    {
        return await db.InTransactionAsync(IssueCoreAsync, cancellationToken);

        async Task<Invitation> IssueCoreAsync(CancellationToken token)
        {
            var user = await RequireAsync(id, token);

            if (!user.IsActive)
            {
                throw new AppException(
                    ErrorCodes.UserInactive, "Учётная запись отключена", HttpStatusCode.Conflict);
            }

            if (user.IsSuperuser)
            {
                EnsureMaySetSuperuser(true);
            }

            var reset = await invitations.IssueAsync(user.Id, Purposes.Reset, cancellationToken: token);

            audit.Write(new AuditEntry(
                IdentityActions.PasswordResetIssued,
                ActorId: actor.Id,
                ResourceType: IdentityResources.User,
                ResourceId: user.Id,
                Changes: new Dictionary<string, object?> { ["expires_at"] = reset.ExpiresAt }));

            var письмо = await mail.PasswordResetAsync(
                user, reset.Token, reset.ExpiresAt, token);

            // ⚠ ⚠ Предел частоты — отказ с откатом, а не ссылка в ответе (решение 0110, разбор 2026-10-21).
            if (письмо == MailQueueResult.RateLimited)
            {
                throw ПисьмоНедавно();
            }

            await db.SaveChangesAsync(token);

            // ⚠ Ссылка возвращается вызывающему ТОЛЬКО если почты нет: иначе секрет уходит двумя каналами вместо одного (решение 0031).
            return письмо == MailQueueResult.Queued ? reset with { Token = string.Empty } : reset;
        }
    }

    /// <summary>Что человек выбрал себе и что будет действовать.</summary>
    public async Task<(string? Chosen, string Effective)> SecondFactorAsync(
        CancellationToken cancellationToken = default)
    {
        var user = await RequireAsync(actor.Id!.Value, cancellationToken);

        return (user.SecondFactorMethod, await methods.ResolveAsync(user, cancellationToken));
    }

    /// <summary>Выбрать себе способ второго фактора.</summary>
    public async Task ChooseSecondFactorAsync(
        string? method, CancellationToken cancellationToken = default)
    {
        if (!SecondFactorPolicy.Выбираем(method))
        {
            throw new AppException(
                ErrorCodes.Validation,
                "Такого способа второго фактора выбрать нельзя.",
                HttpStatusCode.UnprocessableContent,
                new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["allowed"] = SecondFactorMethods.Choosable,
                });
        }

        await db.InTransactionAsync(
            async token =>
            {
                var user = await RequireAsync(actor.Id!.Value, token);
                var прежний = user.SecondFactorMethod;

                user.SecondFactorMethod = method;
                user.UpdatedAt = clock.GetUtcNow();

                // ⚠ ⚠ Смена способа гасит доверие машинам (ревью 2026-09-15, S2, trust-1).
                if (!string.Equals(прежний, method, StringComparison.Ordinal))
                {
                    await invitations.QuenchAsync(user.Id, Purposes.TrustedDevice, user.UpdatedAt, token);
                }

                audit.Write(new AuditEntry(
                    IdentityActions.TwoFactorMethodChanged,
                    ActorId: actor.Id,
                    ResourceType: IdentityResources.User,
                    ResourceId: user.Id,
                    Changes: new Dictionary<string, object?> { ["method"] = method }));

                await db.SaveChangesAsync(token);
            },
            cancellationToken);
    }

    /// <summary>Публичная просьба о ссылке на смену пароля.</summary>
    public async Task RequestPasswordResetAsync(
        string email, CancellationToken cancellationToken = default)
    {
        var адрес = Normalize(email);

        // ⚠ Отключённому не шлём: ссылка ему всё равно не поможет — сброс пароля отказывает отключённой учётной записи.
        if (await users.FindByEmailAsync(адрес, cancellationToken) is not { IsActive: true })
        {
            // ⚠ Холостая работа, чтобы «нет такого адреса» не отличалось по времени от «есть, выдали ссылку» (найдено ревью 23.8).
            await passwords.WasteVerificationTimeAsync(cancellationToken);

            return;
        }

        // ⚠ ⚠ И на живой ветке — та же холостая работа (ревизия 2026-10-26, решение 0118).
        await passwords.WasteVerificationTimeAsync(cancellationToken);

        try
        {
            await db.InTransactionAsync(
                async token =>
                {
                    var user = await users.FindByEmailAsync(адрес, token);

                    if (user is null || !user.IsActive)
                    {
                        return;
                    }

                    var reset = await invitations.IssueAsync(
                        user.Id, Purposes.Reset, cancellationToken: token);

                    if (await mail.PasswordResetAsync(user, reset.Token, reset.ExpiresAt, token) != MailQueueResult.Queued)
                    {
                        throw new НеОтправлено();
                    }

                    audit.Write(new AuditEntry(
                        IdentityActions.PasswordResetIssued,
                        ResourceType: IdentityResources.User,
                        ResourceId: user.Id,
                        Changes: new Dictionary<string, object?> { ["by_request"] = true }));

                    await db.SaveChangesAsync(token);
                },
                cancellationToken);
        }
        catch (НеОтправлено)
        {
        }
    }

    /// <summary>Ссылка проиграла гонку или человека отключили: откатываем и пишем отказ снаружи.</summary>
    private sealed class СсылкаОтвергнута : Exception;

    /// <summary>Письмо не встало в очередь: откатываем выданную ссылку и молчим наружу.</summary>
    private sealed class НеОтправлено : Exception;

    /// <summary>Задать пароль по ссылке сброса.</summary>
    public async Task<User> ResetPasswordAsync(
        string token, string password, CancellationToken cancellationToken = default)
    {
        var owner = await invitations.PeekAsync(token, Purposes.Reset, cancellationToken);
        var user = owner is { } id ? await users.FindAsync(id, cancellationToken) : null;

        if (user is not { IsActive: true })
        {
            throw await RejectActivationAsync(cancellationToken);
        }

        await policy.EnsureAsync(password, cancellationToken);

        var subject = user.Id;

        var хеш = await passwords.HashAsync(password, cancellationToken);

        try
        {
            return await db.InTransactionAsync(ResetCoreAsync, cancellationToken);
        }
        catch (СсылкаОтвергнута)
        {
            db.ChangeTracker.Clear();

            throw await RejectActivationAsync(CancellationToken.None);
        }

        async Task<User> ResetCoreAsync(CancellationToken inner)
        {
            var consumed = await invitations.ConsumeAsync(token, Purposes.Reset, inner);

            // ⚠ Отказ — меткой наружу, а не строкой журнала здесь (ревизия 2026-10-26, решение 0118).
            if (consumed != subject)
            {
                throw new СсылкаОтвергнута();
            }

            var fresh = await RequireAsync(subject, inner);
            if (!fresh.IsActive) throw new СсылкаОтвергнута();

            await ApplyNewPasswordAsync(fresh, хеш, byReset: true, inner);

            // ⚠ Сброс у приглашённого — это и активация (ревизия 2026-10-26, решение 0118).
            fresh.ActivatedAt ??= clock.GetUtcNow();
            await invitations.QuenchAsync(fresh.Id, Purposes.Activation, clock.GetUtcNow(), inner);

            // ⚠ ⚠ Сброс — восстановление, а не смена: гаснут и ключи интеграции (решение 0110).
            await tokens.RevokeAllAsync(
                fresh.Id, RevokeReasons.PasswordChanged, TokenKindMask.Everything, inner);

            // ⚠ Уведомление внутри той же транзакции (подэтап 15.5.10a): откат сброса заберёт и его.
            await notices.PasswordResetAsync(fresh.Id, inner);

            await db.SaveChangesAsync(inner);

            return fresh;
        }
    }

    /// <summary>Общая часть: новый хеш, отметки, строка журнала.</summary>
    private async Task ApplyNewPasswordAsync(
        User user, string hash, bool byReset, CancellationToken cancellationToken, bool fromConsole = false)
    {
        var now = clock.GetUtcNow();

        user.PasswordHash = hash;
        user.PasswordChangedAt = now;
        user.UpdatedAt = now;

        user.MustChangePassword = false;

        user.FailedLoginCount = 0;
        user.LockedUntil = null;
        user.LockoutLevel = 0;

        // ⚠ Выданная расписка второго фактора гаснет вместе с паролем.
        await invitations.QuenchAsync(user.Id, Purposes.TwoFactor, now, cancellationToken);

        // ⚠ ⚠ ⚠ ДОВЕРИЕ МАШИНАМ ГАСИТСЯ ЗДЕСЬ, А НЕ У ВЫЗЫВАЮЩЕГО — и до этапа 59.2 гасил только один вызывающий из двух.
        await invitations.QuenchAsync(user.Id, Purposes.TrustedDevice, now, cancellationToken);
        await invitations.QuenchAsync(user.Id, Purposes.LoginCode, now, cancellationToken);
        await invitations.QuenchAsync(user.Id, Purposes.StepUpCode, now, cancellationToken);

        var changes = new Dictionary<string, object?> { ["by_reset"] = byReset };
        if (fromConsole) changes["via_cli"] = true;

        audit.Write(new AuditEntry(
            IdentityActions.PasswordChanged,
            ActorId: fromConsole ? null : user.Id,
            ActorType: fromConsole ? AuditActorType.System : null,
            ResourceType: IdentityResources.User,
            ResourceId: user.Id,
            Changes: changes));
    }

    /// <summary>Изменить своё имя.</summary>
    public Task<User> UpdateOwnProfileAsync(
        string? displayName, CancellationToken cancellationToken = default)
    {
        if (actor.Id is not { } id)
        {
            throw new AppException(
                ErrorCodes.Unauthenticated, "Требуется вход", HttpStatusCode.Unauthorized);
        }

        return UpdateAsync(id, new UserPatch(DisplayName: displayName), cancellationToken);
    }

    /// <summary>Снять блокировку входа.</summary>
    public async Task<User> UnlockAsync(Guid id, CancellationToken cancellationToken = default)
    {
        return await db.InTransactionAsync(UnlockCoreAsync, cancellationToken);

        async Task<User> UnlockCoreAsync(CancellationToken token)
        {
            var user = await RequireAsync(id, token);

            EnsureMayManage(actor, user);

            if (user.LockedUntil is null && user.FailedLoginCount == 0 && user.LockoutLevel == 0)
            {
                return user;
            }

            user.LockedUntil = null;
            user.FailedLoginCount = 0;
            user.LockoutLevel = 0;
            user.UpdatedAt = clock.GetUtcNow();

            audit.Write(new AuditEntry(
                IdentityActions.UserUnlocked,
                ActorId: actor.Id,
                ResourceType: IdentityResources.User,
                ResourceId: user.Id));

            await db.SaveChangesAsync(token);

            return user;
        }
    }

    /// <summary>Закрыть все сессии человека, не отключая его самого.</summary>
    public Task<int> RevokeSessionsAsync(Guid id, CancellationToken cancellationToken = default) =>
        db.InTransactionAsync(
            async token =>
            {
                var user = await RequireAsync(id, token);

                EnsureMayManage(actor, user);

                // ⚠ Здесь маска ПОЛНАЯ, в отличие от смены пароля.
                await invitations.QuenchAsync(user.Id, Purposes.TrustedDevice, clock.GetUtcNow(), token);
                await invitations.QuenchAsync(user.Id, Purposes.TwoFactor, clock.GetUtcNow(), token);
                await invitations.QuenchAsync(user.Id, Purposes.LoginCode, clock.GetUtcNow(), token);
                await invitations.QuenchAsync(user.Id, Purposes.StepUpCode, clock.GetUtcNow(), token);
                return await tokens.RevokeAllAsync(
                    user.Id, RevokeReasons.Revoked, TokenKindMask.Everything, token);
            },
            cancellationToken);

    /// <summary>Включить обратно.</summary>
    public async Task<User> EnableAsync(Guid id, CancellationToken cancellationToken = default)
    {
        return await db.InTransactionAsync(EnableCoreAsync, cancellationToken);

        async Task<User> EnableCoreAsync(CancellationToken token)
        {
            var user = await RequireAsync(id, token);

            EnsureMayManage(actor, user);

            if (user.IsActive)
            {
                return user;
            }

            user.IsActive = true;
            user.UpdatedAt = clock.GetUtcNow();

            audit.Write(new AuditEntry(
                IdentityActions.UserEnabled, ActorId: actor.Id, ResourceType: IdentityResources.User, ResourceId: user.Id));

            await db.SaveChangesAsync(token);

            return user;
        }
    }

    /// <summary>Отключить учётную запись.</summary>
    public async Task<User> DisableAsync(Guid id, CancellationToken cancellationToken = default)
    {
        return await db.InTransactionAsync(DisableCoreAsync, cancellationToken);

        async Task<User> DisableCoreAsync(CancellationToken token)
        {
            var user = await RequireAsync(id, token);

            EnsureMayManage(actor, user);

            if (!user.IsActive)
            {
                return user;
            }

            await EnsureNotLastSuperuserAsync(user, token);

            user.IsActive = false;
            user.UpdatedAt = clock.GetUtcNow();

            await tokens.RevokeAllAsync(user.Id, RevokeReasons.UserInactive, TokenKindMask.Everything, token);
            await db.Set<IdentityToken>().Where(row => row.UserId == user.Id && row.UsedAt == null)
                .ExecuteUpdateAsync(set => set.SetProperty(row => row.UsedAt, user.UpdatedAt), token);

            // ⚠ Отключение гасит ВСЁ: сессии, машины, ключи интеграции (`Everything`) и все одноразовые строки — ссылки, расписки, коды.

            audit.Write(new AuditEntry(
                IdentityActions.UserDisabled, ActorId: actor.Id, ResourceType: IdentityResources.User, ResourceId: user.Id));

            // ⚠ В ТОЙ ЖЕ транзакции, что и само отключение.
            await mail.AccountSuspendedAsync(user, token);

            await db.SaveChangesAsync(token);

            return user;
        }
    }

    /// <summary>Удалить учётную запись мягко: адрес освобождается, журнал о ней остаётся.</summary>
    public async Task DeleteAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await db.InTransactionAsync(
            async token =>
            {
                var user = await RequireAsync(id, token);

                EnsureMayManage(actor, user);

                await EnsureNotLastSuperuserAsync(user, token);

                var now = clock.GetUtcNow();

                user.DeletedAt = now;
                user.IsActive = false;
                user.UpdatedAt = now;

                // ⚠ УДАЛЯЕМ, а не гасим, и удаляем ВСЁ — решение владельца 2026-08-25.
                await tokens.DeleteAllAsync(user.Id, token);

                var swept = await SweepAsync(user.Id, now, token);

                var changes = new Dictionary<string, object?> { ["email"] = user.Email };

                foreach (var (что, сколько) in swept.Access)
                {
                    changes[что] = сколько;
                }

                changes["two_factor"] = swept.TwoFactor;
                changes["links"] = swept.Links;

                audit.Write(new AuditEntry(
                    IdentityActions.UserDeleted,
                    ActorId: actor.Id,
                    ResourceType: IdentityResources.User,
                    ResourceId: user.Id,
                    Changes: changes));

                await db.SaveChangesAsync(token);

                return true;
            },
            cancellationToken);
    }

    /// <summary>Выслать приглашение заново.</summary>
    public Task<Invitation> ResendInvitationAsync(
        Guid id, CancellationToken cancellationToken = default) =>
        db.InTransactionAsync(token => ResendCoreAsync(id, token), cancellationToken);

    private async Task<Invitation> ResendCoreAsync(Guid id, CancellationToken cancellationToken)
    {
        var user = await RequireAsync(id, cancellationToken);

        if (user.ActivatedAt is not null)
        {
            throw new AppException(
                IdentityErrorCodes.UserAlreadyActivated, "Учётная запись уже активирована", HttpStatusCode.Conflict);
        }

        if (!user.IsActive)
        {
            throw new AppException(
                ErrorCodes.UserInactive, "Учётная запись отключена", HttpStatusCode.Conflict);
        }

        // ⚠ Ссылкой на суперпользовательскую учётку распоряжается только суперпользователь.
        if (user.IsSuperuser)
        {
            EnsureMaySetSuperuser(true);
        }

        var invitation = await invitations.IssueAsync(user.Id, Purposes.Activation, cancellationToken: cancellationToken);

        audit.Write(new AuditEntry(
            IdentityActions.InvitationSent,
            ActorId: actor.Id,
            ResourceType: IdentityResources.User,
            ResourceId: user.Id,
            Changes: new Dictionary<string, object?>
            {
                ["expires_at"] = invitation.ExpiresAt,
                ["resent"] = true,
            }));

        var письмо = await mail.InvitationAsync(user, invitation.Token, invitation.ExpiresAt, cancellationToken);

        // ⚠ Предел частоты — отказ, а не ссылка (решение 0110).
        if (письмо == MailQueueResult.RateLimited)
        {
            throw ПисьмоНедавно();
        }

        await db.SaveChangesAsync(cancellationToken);

        return письмо == MailQueueResult.Queued ? invitation with { Token = string.Empty } : invitation;
    }

    /// <summary>Письмо этому человеку уже уходило недавно: ссылку не показываем, ждём.</summary>
    private static AppException ПисьмоНедавно() => new(
        ErrorCodes.RateLimited,
        "Письмо этому человеку уже отправляли недавно. Повторите позже.",
        HttpStatusCode.TooManyRequests);

    /// <summary>Проверить действующую ссылку сброса до показа формы; ссылку не погашает.</summary>
    public async Task<bool> CanResetPasswordAsync(string token, CancellationToken cancellationToken = default)
    {
        var owner = await invitations.PeekAsync(token, Purposes.Reset, cancellationToken);
        return owner is { } id && await users.FindAsync(id, cancellationToken) is { IsActive: true, DeletedAt: null };
    }

    /// <summary>Кого приглашает эта ссылка.</summary>
    public async Task<User?> PeekActivationAsync(string token, CancellationToken cancellationToken = default)
    {
        var owner = await invitations.PeekAsync(token, Purposes.Activation, cancellationToken);

        if (owner is not { } id)
        {
            return null;
        }

        var user = await users.FindAsync(id, cancellationToken);

        return user is { IsActive: true, ActivatedAt: null } ? user : null;
    }

    /// <summary>Активировать учётную запись по ссылке.</summary>
    public async Task<User> ActivateAsync(
        string token, string password, CancellationToken cancellationToken = default)
    {
        var user = await PeekActivationAsync(token, cancellationToken)
            ?? throw await RejectActivationAsync(cancellationToken);

        await policy.EnsureAsync(password, cancellationToken);

        var hash = await passwords.HashAsync(password, cancellationToken);

        try
        {
            return await db.InTransactionAsync(ActivateCoreAsync, cancellationToken);
        }
        catch (СсылкаОтвергнута)
        {
            db.ChangeTracker.Clear();

            throw await RejectActivationAsync(CancellationToken.None);
        }

        async Task<User> ActivateCoreAsync(CancellationToken inner)
        {
            var owner = await invitations.ConsumeAsync(token, Purposes.Activation, inner);

            if (owner != user.Id)
            {
                throw new СсылкаОтвергнута();
            }

            var now = clock.GetUtcNow();

            // ⚠ Перечитываем внутри попытки.
            var fresh = await RequireAsync(user.Id, inner);
            if (!fresh.IsActive || fresh.ActivatedAt is not null) throw new СсылкаОтвергнута();

            fresh.PasswordHash = hash;
            fresh.PasswordChangedAt = now;
            fresh.ActivatedAt = now;
            fresh.MustChangePassword = false;
            fresh.FailedLoginCount = 0;
            fresh.LockedUntil = null;
            fresh.LockoutLevel = 0;
            fresh.UpdatedAt = now;

            // ⚠ Пароль задан — прежние сессии, доверие и расписки гаснут, как у любой смены пароля (решение 0118).
            await tokens.RevokeAllAsync(fresh.Id, RevokeReasons.PasswordChanged, TokenKindMask.Everything, inner);
            await invitations.QuenchAsync(fresh.Id, Purposes.TwoFactor, now, inner);
            await invitations.QuenchAsync(fresh.Id, Purposes.TrustedDevice, now, inner);
            await invitations.QuenchAsync(fresh.Id, Purposes.LoginCode, now, inner);
            await invitations.QuenchAsync(fresh.Id, Purposes.StepUpCode, now, inner);

            audit.Write(new AuditEntry(
                IdentityActions.UserActivated,
                ActorId: fresh.Id,
                ResourceType: IdentityResources.User,
                ResourceId: fresh.Id));

            await db.SaveChangesAsync(inner);

            return fresh;
        }
    }

    /// <summary>Завести первого суперпользователя командой.</summary>
    public async Task<User> BootstrapSuperuserAsync(
        string email, string? displayName, string password, CancellationToken cancellationToken = default)
    {
        var address = NormalizeForCreation(email);

        await policy.EnsureAsync(password, cancellationToken);

        if (await users.EmailTakenAsync(address, cancellationToken))
        {
            throw new AppException(
                IdentityErrorCodes.UserAlreadyExists, $"Пользователь {address} уже существует", HttpStatusCode.Conflict);
        }

        var now = clock.GetUtcNow();

        var user = new User
        {
            Id = Guid.CreateVersion7(),
            Email = address,
            DisplayName = displayName,
            PasswordHash = await passwords.HashAsync(password, cancellationToken),
            PasswordChangedAt = now,
            IsActive = true,
            IsSuperuser = true,
            MustChangePassword = false,
            ActivatedAt = now,
            CreatedAt = now,
            UpdatedAt = now,
        };

        users.Add(user);

        await invitations.IssueAsync(
            user.Id, Purposes.SuperuserSetup, options.SuperuserSetupWindow, cancellationToken: cancellationToken);

        audit.Write(new AuditEntry(
            IdentityActions.SuperuserBootstrapped,
            ActorId: user.Id,
            ActorType: AuditActorType.System,
            ResourceType: IdentityResources.User,
            ResourceId: user.Id,
            Changes: new Dictionary<string, object?> { ["email"] = address }));

        await db.SaveChangesAsync(cancellationToken);

        return user;
    }

    /// <summary>Сменить пароль существующего суперпользователя из консоли установки (0084).</summary>
    public Task<User> SetSuperuserPasswordAsync(
        string email, string password, CancellationToken cancellationToken = default) =>
        db.InTransactionAsync(async token =>
        {
            var user = await RequireSuperuserForRecoveryAsync(email, token);
            await policy.EnsureAsync(password, token);
            await ApplyNewPasswordAsync(user, await passwords.HashAsync(password, token), byReset: true, token, fromConsole: true);
            var now = clock.GetUtcNow();
            await invitations.QuenchAsync(user.Id, Purposes.Reset, now, token);
            await invitations.QuenchAsync(user.Id, Purposes.Activation, now, token);

            if (user.TwoFactorConfiguredAt is null)
            {
                await invitations.IssueAsync(
                    user.Id, Purposes.SuperuserSetup, options.SuperuserSetupWindow, cancellationToken: token);
            }

            await tokens.RevokeAllAsync(user.Id, RevokeReasons.PasswordChanged, TokenKindMask.Everything, token);
            await db.SaveChangesAsync(token);
            return user;
        }, cancellationToken);

    /// <summary>Пароль из консоли для восстановления доступа любой живой учётной записи (решение 0120).</summary>
    internal Task<(User User, int Sessions)> SetPasswordForRecoveryAsync(
        string email, string password, CancellationToken cancellationToken = default) =>
        db.InTransactionAsync(async token =>
        {
            var user = await users.FindByEmailAsync(NormalizeForCreation(email), token)
                ?? throw AppException.NotFound("Учётная запись не найдена");

            await policy.EnsureAsync(password, token);

            await ApplyNewPasswordAsync(user, await passwords.HashAsync(password, token), byReset: true, token, fromConsole: true);

            var now = clock.GetUtcNow();

            user.ActivatedAt ??= now;
            await invitations.QuenchAsync(user.Id, Purposes.Reset, now, token);
            await invitations.QuenchAsync(user.Id, Purposes.Activation, now, token);

            var sessions = await tokens.RevokeAllAsync(
                user.Id, RevokeReasons.PasswordChanged, TokenKindMask.Sessions | TokenKindMask.Devices, token);
            await tokens.RevokeAllAsync(user.Id, RevokeReasons.PasswordChanged, TokenKindMask.Everything, token);

            await db.SaveChangesAsync(token);

            return (user, sessions);
        }, cancellationToken);

    /// <summary>Найти цель консольного восстановления без повышения или включения учётной записи.</summary>
    internal async Task<User> RequireSuperuserForRecoveryAsync(string email, CancellationToken cancellationToken)
    {
        var user = await users.FindByEmailAsync(NormalizeForCreation(email), cancellationToken)
            ?? throw AppException.NotFound("Суперпользователь не найден");
        EnsureSuperuserRecoveryTarget(user);
        return user;
    }

    /// <summary>Команда восстановления не предназначена для обычных пользователей.</summary>
    internal static void EnsureSuperuserRecoveryTarget(User user)
    {
        if (!user.IsSuperuser)
        {
            throw new AppException(ErrorCodes.SuperuserFlagForbidden,
                "Команда применяется только к суперпользователю", HttpStatusCode.Forbidden);
        }
    }

    /// <summary>Заведомо непроверяемый хеш: «пароль есть, но его не знает никто».</summary>
    internal const string Unusable = "!";

    private async Task<User> RequireAsync(Guid id, CancellationToken cancellationToken) =>
        await users.FindAsync(id, cancellationToken)
        ?? throw AppException.NotFound("Пользователь не найден");

    /// <summary>Учётной записью суперпользователя распоряжается только суперпользователь.</summary>
    internal static void EnsureMayManage(ICurrentUser actor, User target)
    {
        if (target.IsSuperuser && !actor.IsSuperuser)
        {
            throw new AppException(
                ErrorCodes.SuperuserFlagForbidden,
                "Учётной записью суперпользователя распоряжается только суперпользователь",
                HttpStatusCode.Forbidden,
                new Dictionary<string, object?> { ["target_is_superuser"] = true });
        }
    }

    /// <summary>Флагом наибольших прав распоряжается только тот, у кого он есть.</summary>
    private void EnsureMaySetSuperuser(bool requested)
    {
        if (!actor.IsSuperuser)
        {
            throw new AppException(
                ErrorCodes.SuperuserFlagForbidden,
                "Флагом суперпользователя распоряжается только суперпользователь",
                HttpStatusCode.Forbidden,
                new Dictionary<string, object?> { ["requested"] = requested });
        }
    }

    /// <summary>В системе обязан оставаться хотя бы один активный суперпользователь.</summary>
    private async Task EnsureNotLastSuperuserAsync(User user, CancellationToken cancellationToken)
    {
        if (!user.IsSuperuser || !user.IsActive)
        {
            return;
        }

        // ⚠ Считать ли неактивированные приглашения.
        var active = await users.LockActiveSuperusersAsync(options.LastSuperuserCountsOnlySignInCapable, cancellationToken);

        if (active.Count == 1 && active.Contains(user.Id))
        {
            throw new AppException(
                IdentityErrorCodes.LastSuperadmin,
                "Нельзя отключить, разжаловать или удалить последнего активного суперпользователя",
                HttpStatusCode.Conflict);
        }
    }

    /// <summary>Отказ в активации — одинаковый снаружи и записанный внутри.</summary>
    private async Task<AppException> RejectActivationAsync(CancellationToken cancellationToken)
    {
        audit.Write(new AuditEntry(
            IdentityActions.ActivationRejected,
            ActorType: AuditActorType.System,
            Result: AuditResult.Deny,
            ResourceType: IdentityResources.User,
            Reason: "ссылка недействительна, просрочена или уже использована"));

        await db.SaveChangesAsync(CancellationToken.None);

        return new AppException(
            IdentityErrorCodes.ActivationTokenInvalid,
            "Ссылка недействительна или уже использована. Попросите администратора прислать новую.",
            HttpStatusCode.BadRequest);
    }

    /// <summary>Что унесли за удалённым человеком.</summary>
    private readonly record struct Swept(IReadOnlyDictionary<string, int> Access, int TwoFactor, int Links);

    /// <summary>Унести за удалённым человеком всё, что продолжало бы что-то значить.</summary>
    private async Task<Swept> SweepAsync(Guid userId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        // ⚠ Роли, членство и выданное лично — у модуля доступа (решение 0108), и уносит их он, в этой же транзакции.
        var access = await accountAccess.RemoveAsync(userId, cancellationToken);

        var factor = await db.Set<UserTwoFactor>()
            .Where(row => row.UserId == userId)
            .ExecuteDeleteAsync(cancellationToken);

        var links = await db.Set<IdentityToken>()
            .Where(row => row.UserId == userId && row.UsedAt == null && row.ExpiresAt > now)
            .ExecuteUpdateAsync(set => set.SetProperty(row => row.UsedAt, now), cancellationToken);

        return new Swept(access, factor, links);
    }

    /// <summary>Привести адрес к виду, в котором он хранится.</summary>
    private static string Normalize(string email) => email.Trim();

    /// <summary>Привести адрес и убедиться, что это адрес.</summary>
    private static string NormalizeForCreation(string email)
    {
        var address = Normalize(email);

        return ЭтоАдрес.IsValid(address)
            ? address
            : throw new AppException(
                ErrorCodes.Validation,
                "Адрес учётной записи не похож на адрес почты",
                System.Net.HttpStatusCode.BadRequest);
    }

    /// <summary>Один экземпляр проверки на процесс: атрибут не хранит состояния.</summary>
    private static readonly EmailAddressAttribute ЭтоАдрес = new();
}
