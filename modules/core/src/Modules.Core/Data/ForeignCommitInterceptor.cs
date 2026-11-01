using System.Data.Common;
using Modules.Core.Errors;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging;

namespace Modules.Core.Data;

/// <summary>Выполняет отложенное после фиксации чужой единицы работы и выбрасывает — после отката.</summary>
public sealed class ForeignCommitInterceptor(
    ForeignCommitQueue queue, ILogger<ForeignCommitInterceptor> log)
    : IDbTransactionInterceptor, ISaveChangesInterceptor
{
    /// <summary>Фиксация чужой транзакции — момент, ради которого всё и заведено.</summary>
    public async Task TransactionCommittedAsync(
        DbTransaction transaction,
        TransactionEndEventData eventData,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(eventData);

        if (eventData.Context is { } context)
        {
            await ВыполнитьПослеФиксацииAsync(context, eventData.TransactionId);
        }
    }

    /// <summary>Сохранение <b>без транзакции</b> — тоже момент, после которого записанное durable.</summary>
    public async ValueTask<int> SavedChangesAsync(
        SaveChangesCompletedEventData eventData, int result, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(eventData);

        if (eventData.Context is { Database.CurrentTransaction: null } context)
        {
            await ВыполнитьПослеФиксацииAsync(context, null);
        }

        return result;
    }

    private async Task ВыполнитьПослеФиксацииAsync(DbContext context, Guid? транзакция)
    {
        foreach (var работа in queue.Take(context, транзакция))
        {
            try
            {
                await работа(CancellationToken.None);
            }
            catch (Exception error)
            {
                log.LogError("after_foreign_commit_failed {Error}", SafeError.Describe(error));
            }
        }
    }
}
