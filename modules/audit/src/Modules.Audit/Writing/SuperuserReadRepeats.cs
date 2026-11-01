using System.Collections.Concurrent;

namespace Modules.Audit.Writing;

/// <summary>Какие обходы суперпользователя на чтении уже записаны: сессия и право — когда.</summary>
internal sealed class SuperuserReadRepeats(TimeProvider clock)
{
    /// <summary>Окно, в котором повтор того же чтения не пишется.</summary>
    public static readonly TimeSpan Window = TimeSpan.FromMinutes(15);

    /// <summary>Больше этого отметок не держим: старые вычищаются при записи.</summary>
    private const int Предел = 10_000;

    private readonly ConcurrentDictionary<(Guid Кто, string Право), DateTimeOffset> _записано = new();

    /// <summary>Записано ли это чтение в пределах окна.</summary>
    public bool Записано(Guid кто, string право) =>
        _записано.TryGetValue((кто, право), out var когда) && clock.GetUtcNow() - когда < Window;

    /// <summary>Отметить, что строка записана сейчас.</summary>
    public void Отметить(Guid кто, string право)
    {
        var теперь = clock.GetUtcNow();

        _записано[(кто, право)] = теперь;

        if (_записано.Count > Предел)
        {
            foreach (var пара in _записано)
            {
                if (теперь - пара.Value >= Window)
                {
                    _записано.TryRemove(пара.Key, out _);
                }
            }
        }
    }
}
