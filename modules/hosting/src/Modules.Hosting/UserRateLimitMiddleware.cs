using System.Threading.RateLimiting;
using Modules.Core.Contracts.Authz;

namespace Modules.Hosting;

/// <summary>Предел частоты на человека — для дорогих действий того, кто уже вошёл.</summary>
public sealed class UserRateLimitMiddleware(RequestDelegate next, ModuleRateLimits limits)
{
    private readonly PartitionedRateLimiter<(string Класс, int Предел, Guid Кто)> _пределы =
        PartitionedRateLimiter.Create<(string Класс, int Предел, Guid Кто), (string, int, Guid)>(
            ключ => RateLimitPartition.GetFixedWindowLimiter(
                ключ,
                свой => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = свой.Item2,
                    Window = TimeSpan.FromMinutes(1),

                    QueueLimit = 0,
                }));

    /// <summary>Пропустить или отказать 429.</summary>
    public async Task InvokeAsync(HttpContext context, ICurrentUser user)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(user);

        if (limits.Of(context.Request.Path) is { Policy.PerUser: { } предел } найдено && user.Id is { } кто)
        {
            using var разрешение = _пределы.AttemptAcquire((найдено.Class, предел, кто));

            if (!разрешение.IsAcquired)
            {
                await ModuleRateLimits.RejectAsync(
                    context,
                    разрешение.TryGetMetadata(MetadataName.RetryAfter, out var через) ? через : null,
                    context.RequestAborted);

                return;
            }
        }

        await next(context);
    }
}
