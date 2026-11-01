using Modules.Identity.Domain;
using Modules.Core.Data;
using Modules.Core.Errors;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Modules.Identity.Users;

/// <summary>Говорит при старте, если у суперпользователя нет второго фактора.</summary>
internal sealed class UnprotectedSuperuserWarning(
    IServiceScopeFactory scopes,
    ILogger<UnprotectedSuperuserWarning> log) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

            var беззащитные = await db.Set<User>()
                .AsNoTracking()
                .Where(user => user.IsSuperuser && user.IsActive && user.DeletedAt == null)
                .Where(user => !db.Set<UserTwoFactor>()
                    .Any(factor => factor.UserId == user.Id && factor.ConfirmedAt != null))
                .CountAsync(cancellationToken);

            if (беззащитные == 0)
            {
                return;
            }

            // ⚠ Число, а не адреса.
            log.LogWarning(
                "superuser_without_authenticator {Count} — для административных учётных записей "
                + "не подключено приложение кодов. Вход требует код по настроенной почте; "
                + "без доступного второго фактора разрешена только начальная настройка. Решение 0082",
                беззащитные);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            // ⚠ Не роняем старт.
            log.LogError("superuser_check_failed {Error}", SafeError.Describe(error));
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
