using Microsoft.EntityFrameworkCore;

namespace Modules.Core.Data;

/// <summary>Одна транзакция на действие — общая для всех модулей.</summary>
public static class Transactions
{
    /// <summary>Выполнить действие в транзакции, повторив при обрыве соединения.</summary>
    public static Task<T> InTransactionAsync<T>(
        this AppDbContext db,
        Func<CancellationToken, Task<T>> action,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(action);

        return db.InTransactionAsync((token, _) => action(token), cancellationToken);
    }

    /// <summary>Зафиксировать; сбой без ответа сервера — «исход неизвестен», и повторять тело нельзя.</summary>
    private static async Task ЗафиксироватьAsync(
        Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction transaction, CancellationToken cancellationToken)
    {
        try
        {
            await transaction.CommitAsync(cancellationToken);
        }
        catch (Exception ошибка) when (ошибка is TimeoutException or System.Data.Common.DbException { SqlState: null })
        {
            throw new InvalidOperationException(
                "Исход фиксации неизвестен: ответ базы потерян, действие могло состояться — повтора не будет.", ошибка);
        }
    }

    /// <summary>То же, но с работой, которую надо сделать <b>после</b> фиксации.</summary>
    public static async Task<T> InTransactionAsync<T>(
        this AppDbContext db,
        Func<CancellationToken, AfterCommit, Task<T>> action,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(action);

        var after = new AfterCommit();

        // ⚠ Транзакция вызывающего уважается, а не подменяется.
        if (db.Database.CurrentTransaction is not null)
        {
            if (!after.Empty)
            {
                throw new InvalidOperationException("Очередь «после фиксации» уже не пуста");
            }

            var nested = await action(cancellationToken, after);

            if (!after.Empty)
            {
                throw new InvalidOperationException(
                    "Действие внутри чужой транзакции отложило работу на фиксацию, а фиксирует "
                    + "не оно. Либо действие зовётся своей транзакцией, либо работа делается "
                    + "не через очередь.");
            }

            return nested;
        }

        var result = await db.Database.CreateExecutionStrategy().ExecuteAsync(
            cancellationToken,
            async (token) =>
            {
                db.ChangeTracker.Clear();
                after.Forget();

                await using var transaction = await db.Database.BeginTransactionAsync(token);

                var attempt = await action(token, after);

                await ЗафиксироватьAsync(transaction, token);

                return attempt;
            });

        // ⚠ ⚠ ⚠ ОТЛОЖЕННОЕ — ЗА ПРЕДЕЛАМИ СТРАТЕГИИ, И ЭТО НЕСУЩЕЕ (ревью 2026-09-15, data-1).
        await after.RunAsync(CancellationToken.None);

        return result;
    }

    /// <summary>То же для действия без результата.</summary>
    public static async Task InTransactionAsync(
        this AppDbContext db,
        Func<CancellationToken, Task> action,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(action);

        await db.InTransactionAsync<bool>(
            async (token) =>
            {
                await action(token);

                return true;
            },
            cancellationToken);
    }
}

/// <summary>Работа, которую надо сделать, когда транзакция уже состоялась.</summary>
public sealed class AfterCommit
{
    private readonly List<Func<CancellationToken, Task>> queued = [];

    /// <summary>Отложить работу до фиксации.</summary>
    public void Then(Func<CancellationToken, Task> work) => queued.Add(work);

    /// <summary>Ничего не отложено.</summary>
    internal bool Empty => queued.Count == 0;

    /// <summary>Забыть отложенное: попытка не удалась и начинается заново.</summary>
    internal void Forget() => queued.Clear();

    /// <summary>Сделать отложенное.</summary>
    internal async Task RunAsync(CancellationToken cancellationToken)
    {
        List<Exception>? errors = null;

        foreach (var work in queued)
        {
            try
            {
                await work(cancellationToken);
            }
            catch (Exception error)
            {
                (errors ??= []).Add(error);
            }
        }

        if (errors is null)
        {
            return;
        }

        if (errors.Count == 1)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Throw(errors[0]);
        }

        throw new AggregateException("Работа после фиксации не удалась", errors);
    }
}
