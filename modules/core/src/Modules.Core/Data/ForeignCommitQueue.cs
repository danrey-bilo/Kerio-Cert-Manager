using System.Runtime.CompilerServices;
using Microsoft.EntityFrameworkCore;

namespace Modules.Core.Data;

/// <summary>Работа, отложенная до фиксации <b>чужой</b> единицы работы.</summary>
public sealed class ForeignCommitQueue
{
    private readonly ConditionalWeakTable<DbContext, List<Отложенное>> _отложено = [];

    /// <summary>Работа вместе с меткой единицы работы, в которой её отложили.</summary>
    private readonly record struct Отложенное(Guid? Транзакция, Func<CancellationToken, Task> Работа);

    /// <summary>Отложить работу до фиксации единицы работы этого контекста.</summary>
    public void Enqueue(DbContext context, Func<CancellationToken, Task> работа)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(работа);

        var метка = context.Database.CurrentTransaction?.TransactionId;
        var очередь = _отложено.GetOrCreateValue(context);

        lock (очередь)
        {
            очередь.Add(new Отложенное(метка, работа));
        }
    }

    /// <summary>Забрать работу закончившейся единицы работы.</summary>
    public IReadOnlyList<Func<CancellationToken, Task>> Take(DbContext context, Guid? транзакция)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (!_отложено.TryGetValue(context, out var очередь))
        {
            return [];
        }

        lock (очередь)
        {
            // ⚠ Метка `null` подходит ЛЮБОЙ фиксации того же контекста, и это не послабление.
            var взято = очередь
                .Where(строка => строка.Транзакция == транзакция || строка.Транзакция is null)
                .Select(строка => строка.Работа)
                .ToArray();

            очередь.Clear();

            return взято;
        }
    }

    /// <summary>Выбросить отложенное, ничего не выполняя.</summary>
    public void Forget(DbContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (_отложено.TryGetValue(context, out var очередь))
        {
            lock (очередь)
            {
                очередь.Clear();
            }
        }
    }
}
