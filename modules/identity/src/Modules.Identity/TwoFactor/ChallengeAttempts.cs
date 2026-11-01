using System.Threading.RateLimiting;
using Modules.Identity.Users;

namespace Modules.Identity.TwoFactor;

/// <summary>Сколько кодов можно предъявить по одной расписке второго шага входа.</summary>
internal sealed class ChallengeAttempts : IDisposable
{
    /// <summary>Окно счёта — не короче наибольшего срока расписки.</summary>
    private static readonly TimeSpan Окно = TimeSpan.FromHours(1);

    private readonly PartitionedRateLimiter<string>? _попытки;

    public ChallengeAttempts(IdentityModuleOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        Limit = options.ChallengeCodeAttempts;

        if (Limit > 0)
        {
            _попытки = PartitionedRateLimiter.Create<string, string>(
                ключ => RateLimitPartition.GetFixedWindowLimiter(
                    ключ,
                    _ => new FixedWindowRateLimiterOptions
                    {
                        PermitLimit = Limit,
                        Window = Окно,
                        QueueLimit = 0,
                    }));
        }
    }

    /// <summary>Сколько кодов можно предъявить по одной расписке; ноль — без предела.</summary>
    public int Limit { get; }

    /// <summary>Занять попытку по расписке.</summary>
    public bool Take(string challenge)
    {
        if (_попытки is null)
        {
            return true;
        }

        using var разрешение = _попытки.AttemptAcquire(Ключ(challenge));

        return разрешение.IsAcquired;
    }

    /// <summary>Кончились ли у расписки попытки.</summary>
    public bool Exhausted(string challenge) =>
        _попытки?.GetStatistics(Ключ(challenge))?.CurrentAvailablePermits is <= 0;

    /// <summary>Ключ — хеш расписки, а не она сама: открытый секрет незачем держать в словаре.</summary>
    private static string Ключ(string challenge) => Invitations.CodeBinding(challenge);

    public void Dispose() => _попытки?.Dispose();
}
