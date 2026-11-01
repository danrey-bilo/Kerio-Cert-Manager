using Modules.Core.Data;
using Modules.Identity.Contracts;
using Modules.Identity.TwoFactor;

namespace Modules.Identity.Users;

/// <summary>Вернуть доступ к учётной записи из консоли — одной транзакцией (решение 0120).</summary>
internal sealed class AccessRecovery(AppDbContext db, UserService people, TwoFactorService factors)
{
    /// <summary>Сменить пароль, снять фактор и блокировку, закрыть сессии — разом.</summary>
    public Task<AccessResetResult> ResetAsync(
        string email, string password, CancellationToken cancellationToken = default) =>
        db.InTransactionAsync(
            async token =>
            {
                var (user, sessions) = await people.SetPasswordForRecoveryAsync(email, password, token);
                var hadFactor = await factors.ResetForRecoveryAsync(user.Id, token);

                return new AccessResetResult(user.Id, user.Email, user.IsActive, hadFactor, sessions);
            },
            cancellationToken);
}
