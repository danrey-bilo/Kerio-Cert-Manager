namespace Modules.Core.Errors;

/// <summary>Описание исключения, которое можно писать в лог.</summary>
public static class SafeError
{
    /// <summary>Сколько звеньев цепочки показывать.</summary>
    private const int MaxDepth = 4;

    /// <summary>Цепочка типов исключения: <c>Внешний → Внутренний → …</c>.</summary>
    public static string Describe(Exception? error)
    {
        if (error is null)
        {
            return "нет";
        }

        var chain = new List<string>(MaxDepth);

        for (var current = error; current is not null && chain.Count < MaxDepth; current = current.InnerException)
        {
            chain.Add(current.GetType().Name);
        }

        return string.Join(" → ", chain);
    }
}
