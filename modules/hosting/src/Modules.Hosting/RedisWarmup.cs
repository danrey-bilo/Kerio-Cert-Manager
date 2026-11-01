using StackExchange.Redis;

namespace Modules.Hosting;

/// <summary>Прогрев мультиплексора Redis на старте — чтобы первую задержку не платил первый запрос.</summary>
public sealed class RedisWarmup(IConnectionMultiplexer redis, ILogger<RedisWarmup> log) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        _ = Task.Run(
            async () =>
            {
                try
                {
                    await redis.GetDatabase().PingAsync();
                }
                catch (Exception error) when (error is RedisException or TimeoutException)
                {
                    log.LogWarning(
                        "redis_warmup_failed {Error}", Modules.Core.Errors.SafeError.Describe(error));
                }
            },
            cancellationToken);

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
