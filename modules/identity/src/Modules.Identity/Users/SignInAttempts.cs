using System.Net;
using System.Threading.RateLimiting;
using Modules.Core.Errors;

namespace Modules.Identity.Users;

/// <summary>Предел попыток входа на пару «адрес + почта».</summary>
internal sealed class SignInAttempts(IdentityModuleOptions options) : IDisposable
{
    private readonly PartitionedRateLimiter<string> _пределы = PartitionedRateLimiter.Create<string, string>(
        ключ => RateLimitPartition.GetFixedWindowLimiter(
            ключ,
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = Math.Max(1, options.SignInAttemptsPerMinute),
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0,
            }));

    /// <summary>Занять попытку или отказать 429 тем же конвертом, что у ограничителя хоста.</summary>
    public void Require(IPAddress? address, string email)
    {
        ArgumentNullException.ThrowIfNull(email);

        using var разрешение = _пределы.AttemptAcquire(
            $"{address?.ToString() ?? "?"}|{email.Trim().ToLowerInvariant()}");

        if (!разрешение.IsAcquired)
        {
            throw new AppException(
                ErrorCodes.RateLimited, "Слишком много попыток. Подождите немного.", HttpStatusCode.TooManyRequests);
        }
    }

    public void Dispose() => _пределы.Dispose();
}
