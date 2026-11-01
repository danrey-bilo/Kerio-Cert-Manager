using Modules.Core.Errors;
using Serilog.Core;
using Serilog.Events;

namespace Modules.Hosting;

/// <summary>Приёмник логов, до которого исключение доезжает цепочкой типов и стеком, но не текстом.</summary>
public sealed class SafeExceptionSink(ILogEventSink inner) : ILogEventSink, IDisposable
{
    /// <summary>Поле с цепочкой типов исключения.</summary>
    public const string TypesProperty = "ExceptionTypes";

    /// <summary>Поле со стеком верхнего исключения.</summary>
    public const string StackProperty = "ExceptionStack";

    public void Emit(LogEvent logEvent)
    {
        ArgumentNullException.ThrowIfNull(logEvent);

        if (logEvent.Exception is not { } error)
        {
            inner.Emit(logEvent);

            return;
        }

        // ⚠ ⚠ Текст исключения едет не только в самом исключении (решение 0110, разбор 2026-10-21, находка 18).
        var тексты = Тексты(error);

        var поля = logEvent.Properties
            .Where(пара => пара.Key is not (TypesProperty or StackProperty))
            .Select(пара => new LogEventProperty(
                пара.Key,
                НесётТекст(пара.Value, тексты) ? new ScalarValue(SafeError.Describe(error)) : пара.Value))
            .Append(new LogEventProperty(TypesProperty, new ScalarValue(SafeError.Describe(error))))
            .ToList();

        if (error.StackTrace is { } стек)
        {
            поля.Add(new LogEventProperty(StackProperty, new ScalarValue(стек)));
        }

        inner.Emit(new LogEvent(
            logEvent.Timestamp,
            logEvent.Level,
            exception: null,
            logEvent.MessageTemplate,
            поля,
            logEvent.TraceId ?? default,
            logEvent.SpanId ?? default));
    }

    /// <summary>Тексты всех исключений цепочки — то, чего в записи быть не должно.</summary>
    private static List<string> Тексты(Exception error)
    {
        var тексты = new List<string>();
        var очередь = new Queue<Exception>([error]);

        while (очередь.Count > 0 && тексты.Count < 32)
        {
            var текущее = очередь.Dequeue();

            if (текущее.Message is { Length: >= 8 } текст)
            {
                тексты.Add(текст);
            }

            if (текущее is AggregateException сводное)
            {
                foreach (var внутреннее in сводное.InnerExceptions)
                {
                    очередь.Enqueue(внутреннее);
                }
            }
            else if (текущее.InnerException is { } внутреннее)
            {
                очередь.Enqueue(внутреннее);
            }
        }

        return тексты;
    }

    /// <summary>Несёт ли значение свойства текст исключения — само исключение или его строку.</summary>
    private static bool НесётТекст(LogEventPropertyValue значение, List<string> тексты)
    {
        if (значение is ScalarValue { Value: Exception })
        {
            return true;
        }

        var строка = значение is ScalarValue { Value: string голая } ? голая : значение.ToString();

        foreach (var текст in тексты)
        {
            if (строка.Contains(текст, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    public void Dispose() => (inner as IDisposable)?.Dispose();
}
