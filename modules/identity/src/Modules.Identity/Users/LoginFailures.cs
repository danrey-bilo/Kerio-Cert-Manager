using Modules.Identity.Contracts;
using Modules.Identity.Domain;
using Modules.Identity.Naming;
using Modules.Identity.Notifications;
using Modules.Identity.Settings;
using Modules.Core.Contracts.Audit;
using Modules.Core.Contracts.Settings;
using Modules.Core.Data;
using Modules.Core.Errors;
using Microsoft.EntityFrameworkCore;
using Modules.Identity.Passwords;

namespace Modules.Identity.Users;

/// <summary>Неудачная попытка доказать, что ты — это ты.</summary>
internal sealed class LoginFailures(
    AppDbContext db,
    ISettingsReader settings,
    IAuditWriter audit,
    SecurityNotices notices)
{
    /// <summary>Взять попытку: засчитать её заранее — или узнать, что учётная запись заперта.</summary>
    public async Task<LoginAttempt?> TakeAsync(
        Guid userId, DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        var threshold = Math.Max(1, await settings.GetAsync(IdentitySettings.LockoutThreshold, cancellationToken));
        var minutes = Math.Max(1, await settings.GetAsync(IdentitySettings.LockoutMinutes, cancellationToken));
        var escalate = await settings.GetAsync(IdentitySettings.LockoutEscalation, cancellationToken);

        var ceiling = Math.Max(minutes, await settings.GetAsync(IdentitySettings.LockoutMaxMinutes, cancellationToken));

        // ⚠ В SET Postgres видит ПРЕЖНЮЮ строку.
        var взята = await db.Database.SqlQuery<LoginAttempt>($"""
            UPDATE users SET
                failed_login_count = CASE WHEN locked_until IS NOT NULL THEN 1 ELSE failed_login_count + 1 END,
                locked_until = CASE
                    WHEN (CASE WHEN locked_until IS NOT NULL THEN 1 ELSE failed_login_count + 1 END) >= {threshold}
                        THEN {now} + make_interval(mins => LEAST({ceiling},
                            {minutes} * (1 << LEAST(CASE WHEN {escalate} THEN lockout_level ELSE 0 END, 16))))
                    ELSE NULL END,
                lockout_level = CASE
                    WHEN {escalate} AND (CASE WHEN locked_until IS NOT NULL THEN 1 ELSE failed_login_count + 1 END) >= {threshold}
                        THEN lockout_level + 1
                    ELSE lockout_level END
            WHERE id = {userId} AND (locked_until IS NULL OR locked_until <= {now})
            RETURNING failed_login_count AS "Count", locked_until AS "LockedUntil"
            """).ToListAsync(cancellationToken);

        return взята.Count == 0 ? null : взята[0];
    }

    /// <summary>Вернуть попытку, когда проверка подошла: верный пароль счётчик не двигает.</summary>
    public async Task ReturnAsync(Guid userId, LoginAttempt attempt, CancellationToken cancellationToken = default)
    {
        if (attempt.LockedUntil is { } своя)
        {
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                UPDATE users SET
                    failed_login_count = GREATEST(failed_login_count - 1, 0),
                    lockout_level = CASE WHEN locked_until = {своя} THEN GREATEST(lockout_level - 1, 0) ELSE lockout_level END,
                    locked_until = CASE WHEN locked_until = {своя} THEN NULL ELSE locked_until END
                WHERE id = {userId}
                """, cancellationToken);

            return;
        }

        await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE users SET failed_login_count = GREATEST(failed_login_count - 1, 0) WHERE id = {userId}
            """, cancellationToken);
    }

    /// <summary>Проверить пароль взятой попыткой.</summary>
    public async Task<bool> VerifyPasswordAsync(
        IPasswordHasher passwords, Guid userId, LoginAttempt attempt, string? hash, string password)
    {
        ArgumentNullException.ThrowIfNull(passwords);

        try
        {
            return await passwords.VerifyAsync(hash, password, CancellationToken.None);
        }
        catch (AppException занято) when (PasswordHasher.Busy(занято))
        {
            await ReturnAsync(userId, attempt, CancellationToken.None);

            throw;
        }
    }

    /// <summary>Сообщить человеку о блокировке, если её поставила именно эта попытка.</summary>
    public async Task NoticeAsync(
        Guid userId, LoginAttempt attempt, string reason, CancellationToken cancellationToken = default)
    {
        if (attempt.LockedUntil is not null)
        {
            await notices.LockedOutAsync(userId, reason, CancellationToken.None);
        }
    }

    /// <summary>Записать неудачу взятой попытки: журнал, уведомление о блокировке, сохранение.</summary>
    public async Task RecordAsync(
        User user,
        LoginAttempt attempt,
        string reason,
        CancellationToken cancellationToken = default,
        AuditEntry? след = null)
    {
        audit.Write(след ?? new AuditEntry(
            IdentityActions.LoggedIn,
            ActorId: user.Id,
            Result: AuditResult.Deny,
            ResourceType: IdentityResources.User,
            ResourceId: user.Id,
            Reason: reason));

        // ⚠ ⚠ Третья дверь к той же блокировке, и её плана 15.5.10a в таблице не было.
        await NoticeAsync(user.Id, attempt, reason, cancellationToken);

        // ⚠ Без токена запроса (решение 0118).
        await db.SaveChangesAsync(CancellationToken.None);
    }
}

/// <summary>Взятая попытка доказать личность: счёт после неё и замок, если она его поставила.</summary>
internal sealed class LoginAttempt
{
    /// <summary>Неудач подряд, считая эту.</summary>
    public int Count { get; init; }

    /// <summary>До какого времени заперто — только если заперла эта попытка.</summary>
    public DateTimeOffset? LockedUntil { get; init; }
}
