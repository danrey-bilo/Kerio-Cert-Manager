using Modules.Tokens.Domain;
using Modules.Core.Data;
using Modules.Core.Errors;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Modules.Tokens.Tokens;

/// <summary>Отметка «ключом пользовались» — мимо транзакции вызывающего и не чаще раза в минуту.</summary>
internal sealed class LastSeenWriter(
    IServiceScopeFactory scopes,
    TimeProvider clock,
    ILogger<LastSeenWriter> log)
{
    /// <summary>Как часто отметка вообще может двигаться.</summary>
    private static readonly TimeSpan Порог = TimeSpan.FromMinutes(1);

    private readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, DateTimeOffset> _когда = new();

    /// <summary>Сколько записей дошло до базы.</summary>
    internal int Записей => Volatile.Read(ref _записей);

    private int _записей;

    /// <summary>Отметить использование, если пора.</summary>
    public void Schedule(Guid tokenId, DateTimeOffset lastSeen)
    {
        var now = clock.GetUtcNow();
        var прежде = _когда.GetValueOrDefault(tokenId, lastSeen);

        if (now - прежде < Порог)
        {
            return;
        }

        // ⚠ Отметка ставится ДО записи, а не после.
        if (!_когда.TryUpdate(tokenId, now, прежде) && !_когда.TryAdd(tokenId, now))
        {
            return;
        }

        _ = ЗаписатьAsync(tokenId, now);
    }

    private async Task ЗаписатьAsync(Guid tokenId, DateTimeOffset now)
    {
        try
        {
            await using var scope = scopes.CreateAsyncScope();

            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

            await db.Set<AuthToken>()
                .Where(row => row.Id == tokenId)
                .ExecuteUpdateAsync(setters => setters.SetProperty(row => row.LastSeenAt, now));

            Interlocked.Increment(ref _записей);
        }
        catch (Exception error)
        {
            // ⚠ Через SafeError: строка подключения Npgsql несёт пароль целиком, и текст исключения при сбое подключения вынес бы его в лог (правило 14).
            log.LogWarning("last_seen_failed {Token} {Error}", tokenId, SafeError.Describe(error));
        }
    }
}
