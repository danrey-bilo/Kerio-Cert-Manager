using Modules.Identity.Permissions;
using Modules.Identity.Storage;
using Modules.Identity.TwoFactor;
using Modules.Identity.Users;
using Modules.Core.Contracts.Accounts;
using Modules.Core.Contracts.Authz;
using Modules.Core.Data;
using Modules.Identity.Domain;
using Modules.Core.Errors;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Modules.Identity.Api;

/// <summary>Учётные записи.</summary>
[ApiController]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
[Route("api/users")]
internal sealed class UsersController(
    UserService users,
    UserRepository repository,
    TwoFactorService factors,
    IAccountAccess access,
    AppDbContext db) : ControllerBase
{
    /// <summary>Список пользователей.</summary>
    [HttpGet]
    [RequirePermission(IdentityPermissions.Read)]
    public async Task<UsersPageOut> List(
        [FromQuery(Name = "query")] string? query,
        [FromQuery(Name = "after")] string? after,
        [FromQuery(Name = "limit")] int limit = 50,
        CancellationToken cancellationToken = default)
    {
        var (items, next) = await repository.PageAsync(
            query, UserCursor.Decode(after), limit, cancellationToken);

        var ids = items.Select(user => user.Id).ToArray();

        // ⚠ Роли — у модуля доступа (решение 0108), и видимость решает он своим правом: состав власти закрыт тем же правом, что и список ролей.
        var roleMap = await access.RolesAsync(ids, cancellationToken);
        var confirmed = await db.Set<UserTwoFactor>().Where(row => ids.Contains(row.UserId) && row.ConfirmedAt != null)
            .Select(row => row.UserId).ToListAsync(cancellationToken);
        return new UsersPageOut([.. items.Select(user => UserOut.From(user) with
        {
            Roles = roleMap is null
                ? null
                : [.. (roleMap.TryGetValue(user.Id, out var его) ? его : []).Select(role => new UserRoleOut(role.Id, role.Name))],
            AuthenticatorEnabled = confirmed.Contains(user.Id),
        })], next);
    }

    /// <summary>Профиль одного.</summary>
    [HttpGet("{id:guid}")]
    [RequirePermission(IdentityPermissions.Read)]
    public async Task<UserOut> Get(Guid id, CancellationToken cancellationToken)
    {
        var user = await repository.ReadAsync(id, cancellationToken)
            ?? throw AppException.NotFound("Пользователь не найден");

        var roleMap = await access.RolesAsync([id], cancellationToken);
        var enabled = await db.Set<UserTwoFactor>()
            .AnyAsync(row => row.UserId == id && row.ConfirmedAt != null, cancellationToken);

        return UserOut.From(user) with
        {
            Roles = roleMap is null
                ? null
                : [.. (roleMap.TryGetValue(id, out var его) ? его : []).Select(role => new UserRoleOut(role.Id, role.Name))],
            AuthenticatorEnabled = enabled,
        };
    }

    /// <summary>Завести учётную запись.</summary>
    [HttpPost]
    [RequireRecentSecondFactor]
    [RequirePermission(IdentityPermissions.Create)]
    public async Task<ActionResult<UserCreatedOut>> Create(
        [FromBody] UserIn body, CancellationToken cancellationToken)
    {
        if (body.Password is not null)
            throw new AppException(ErrorCodes.Validation, "Создайте приглашение: пароль сотрудник задаёт сам.", System.Net.HttpStatusCode.BadRequest);
        var (user, invitation) = await db.InTransactionAsync(async token =>
        {
            var created = await users.CreateAsync(new UserDraft(body.Email, body.DisplayName,
                IsSuperuser: body.IsSuperuser, MustChangePassword: false, Department: body.Department), token);
            await access.AssignOnCreateAsync(created.User.Id, body.RoleIds ?? [], token);
            return created;
        }, cancellationToken);

        return Created(
            $"/api/users/{user.Id}",
            new UserCreatedOut(UserOut.From(user), invitation?.Token, invitation?.ExpiresAt));
    }

    /// <summary>Изменить профиль и флаги.</summary>
    [HttpPatch("{id:guid}")]
    [RequireRecentSecondFactor]
    [RequirePermission(IdentityPermissions.Update)]
    public async Task<UserOut> Update(
        Guid id, [FromBody] UserPatchIn body, CancellationToken cancellationToken)
    {
        var user = await users.UpdateAsync(
            id,
            new UserPatch(body.DisplayName, body.IsSuperuser, body.MustChangePassword, body.Department),
            cancellationToken);

        return UserOut.From(user);
    }

    /// <summary>Отключить.</summary>
    [HttpPost("{id:guid}/disable")]
    [RequireRecentSecondFactor]
    [RequirePermission(IdentityPermissions.Disable)]
    public async Task<UserOut> Disable(Guid id, CancellationToken cancellationToken) =>
        UserOut.From(await users.DisableAsync(id, cancellationToken));

    /// <summary>Включить обратно.</summary>
    [HttpPost("{id:guid}/enable")]
    [RequireRecentSecondFactor]
    [RequirePermission(IdentityPermissions.Disable)]
    public async Task<UserOut> Enable(Guid id, CancellationToken cancellationToken) =>
        UserOut.From(await users.EnableAsync(id, cancellationToken));

    /// <summary>Удалить.</summary>
    [HttpDelete("{id:guid}")]
    [RequireRecentSecondFactor]
    [RequirePermission(IdentityPermissions.Delete)]
    public async Task<IActionResult> Delete(Guid id, CancellationToken cancellationToken)
    {
        await users.DeleteAsync(id, cancellationToken);

        return NoContent();
    }

    /// <summary>Снять блокировку входа.</summary>
    [HttpPost("{id:guid}/unlock")]
    [RequireRecentSecondFactor]
    [RequirePermission(IdentityPermissions.Disable)]
    public async Task<UserOut> Unlock(Guid id, CancellationToken cancellationToken) =>
        UserOut.From(await users.UnlockAsync(id, cancellationToken));

    /// <summary>Снять человеку второй фактор.</summary>
    [HttpDelete("{id:guid}/two-factor")]
    [RequireRecentSecondFactor]
    [RequirePermission(IdentityPermissions.ResetTwoFactor)]
    public async Task<IActionResult> ResetTwoFactor(Guid id, CancellationToken cancellationToken)
    {
        await factors.ResetForAsync(id, cancellationToken);
        return NoContent();
    }

    /// <summary>Закрыть все сессии человека, не отключая его.</summary>
    [HttpDelete("{id:guid}/sessions")]
    [RequireRecentSecondFactor]
    [RequirePermission(IdentityPermissions.Disable)]
    public async Task<RevokedOut> RevokeSessions(Guid id, CancellationToken cancellationToken) =>
        new(await users.RevokeSessionsAsync(id, cancellationToken));

    /// <summary>Выдать ссылку сброса пароля.</summary>
    [HttpPost("{id:guid}/reset-password")]
    [RequireRecentSecondFactor]
    [RequirePermission(IdentityPermissions.Create)]
    public async Task<ResetIssuedOut> ResetPassword(Guid id, CancellationToken cancellationToken)
    {
        var reset = await users.IssuePasswordResetAsync(id, cancellationToken);

        // ⚠ Пустая строка от службы означает «письмо ушло»: показывать ссылку второй раз незачем, а `null` говорит это клиенту прямо (решение 0031).
        return new ResetIssuedOut(
            string.IsNullOrEmpty(reset.Token) ? null : reset.Token, reset.ExpiresAt);
    }

    /// <summary>Выслать приглашение заново.</summary>
    [HttpPost("{id:guid}/invite")]
    [RequireRecentSecondFactor]
    [RequirePermission(IdentityPermissions.Create)]
    public async Task<UserCreatedOut> Invite(Guid id, CancellationToken cancellationToken)
    {
        var invitation = await users.ResendInvitationAsync(id, cancellationToken);
        var user = await repository.FindAsync(id, cancellationToken);

        return new UserCreatedOut(UserOut.From(user!),
            string.IsNullOrEmpty(invitation.Token) ? null : invitation.Token, invitation.ExpiresAt);
    }
}
