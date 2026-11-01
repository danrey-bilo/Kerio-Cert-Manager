using Modules.Core.Data;
using Microsoft.EntityFrameworkCore;

namespace Modules.Audit.Writing;

/// <summary>Сохранение строки журнала своим подключением — с пределом ожидания головы цепи.</summary>
internal static class OwnConnection
{
    /// <summary>Сколько ждать голову цепи, прежде чем отказаться.</summary>
    internal const string LockTimeout = "2s";

    /// <summary>Сохранить единицу работы с пределом ожидания замков.</summary>
    public static async Task SaveAsync(AppDbContext db, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);

        await db.Database.OpenConnectionAsync(cancellationToken);

        try
        {
            await db.Database.ExecuteSqlRawAsync($"SET lock_timeout = '{LockTimeout}'", cancellationToken);
            await db.SaveChangesAsync(cancellationToken);
        }
        finally
        {
            try
            {
                await db.Database.ExecuteSqlRawAsync("RESET lock_timeout", CancellationToken.None);
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
            }

            await db.Database.CloseConnectionAsync();
        }
    }
}
