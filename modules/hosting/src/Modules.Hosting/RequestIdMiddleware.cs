namespace Modules.Hosting;

/// <summary>Номер обращения: рождается на входе, попадает в лог, в ответ и в журнал аудита.</summary>
public sealed class RequestIdMiddleware(RequestDelegate next, ILogger<RequestIdMiddleware> log)
{
    /// <summary>Имя заголовка — общее на вход и на выход.</summary>
    public const string HeaderName = "X-Request-ID";

    /// <summary>Ключ в <see cref="HttpContext.Items"/>, откуда номер берут остальные.</summary>
    public const string ItemKey = "request_id";

    public async Task InvokeAsync(HttpContext context)
    {
        var requestId =
            (context.Request.Headers[HeaderName].FirstOrDefault() is { Length: > 0 } incoming
                ? Trim(incoming)
                : null)
            ?? Guid.CreateVersion7().ToString("n");

        context.Items[ItemKey] = requestId;

        context.Response.Headers[HeaderName] = requestId;

        using (Область(log, requestId))
        {
            await next(context);
        }
    }

    /// <summary>Готовая область с номером обращения — ноль словарей на запрос.</summary>
    private static readonly Func<Microsoft.Extensions.Logging.ILogger, string, IDisposable?> Область =
        Microsoft.Extensions.Logging.LoggerMessage.DefineScope<string>("request_id:{RequestId}");

    /// <summary>Чужой номер обрезается и просеивается по составу.</summary>
    private static string? Trim(string value)
    {
        var кусок = value.Length <= 128 ? value : value[..128];

        foreach (var знак in кусок)
        {
            if (!(char.IsAsciiLetterOrDigit(знак) || знак is '-' or '_' or '.' or ':'))
            {
                return null;
            }
        }

        return кусок;
    }
}
