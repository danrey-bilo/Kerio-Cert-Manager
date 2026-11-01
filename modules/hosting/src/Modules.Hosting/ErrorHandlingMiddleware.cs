using System.Net;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Unicode;
using Modules.Core.Errors;

namespace Modules.Hosting;

/// <summary>Единственное место, где исключение превращается в ответ.</summary>
public sealed class ErrorHandlingMiddleware(RequestDelegate next, ILogger<ErrorHandlingMiddleware> log)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Encoder = JavaScriptEncoder.Create(UnicodeRanges.All),
    };

    public async Task InvokeAsync(HttpContext context)
    {
        try
        {
            await next(context);

            await ЗавернутьПустойОтказAsync(context);
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
            log.LogDebug("request_aborted {Path}", context.Request.Path);
        }
        catch (IOException) when (context.RequestAborted.IsCancellationRequested)
        {
            log.LogDebug("request_aborted {Path}", context.Request.Path);
        }
        catch (BadHttpRequestException error)
        {
            log.LogWarning(
                "bad_request {Path} {Status} {Error}", context.Request.Path, error.StatusCode, SafeError.Describe(error));

            await WriteAsync(
                context,
                (HttpStatusCode)error.StatusCode,
                new ErrorResponse(
                    error.StatusCode == StatusCodes.Status413PayloadTooLarge ? ErrorCodes.PayloadTooLarge : ErrorCodes.Validation,
                    error.StatusCode == StatusCodes.Status413PayloadTooLarge
                        ? "Запрос больше разрешённого"
                        : "Запрос не удалось прочитать",
                    Details: null,
                    RequestId(context)),
                context.RequestAborted);
        }
        catch (AppException error)
        {
            await WriteAsync(
                context,
                error.Status,
                new ErrorResponse(error.Code, error.Message, error.Details, RequestId(context)),
                context.RequestAborted);
        }
        catch (Exception error)
        {
            // ⚠ ⚠ ⚠ ЦЕПОЧКА ТИПОВ И СТЕК — ДА, ТЕКСТ — НЕТ, и это последнее место в решении, где так было не всегда.
            log.LogError(
                "unhandled_exception {Path} {Error} {Stack}",
                context.Request.Path,
                SafeError.Describe(error),
                error.StackTrace);

            await WriteAsync(
                context,
                HttpStatusCode.InternalServerError,
                new ErrorResponse(
                    ErrorCodes.Internal,
                    "Внутренняя ошибка. Сообщите администратору номер обращения.",
                    Details: null,
                    RequestId(context)),
                context.RequestAborted);
        }
    }

    /// <summary>Отказ платформы без тела — конвертом: неизвестный адрес, чужой метод, чужой тип.</summary>
    private static async Task ЗавернутьПустойОтказAsync(HttpContext context)
    {
        var ответ = context.Response;

        if (ответ.HasStarted
            || ответ.StatusCode < 400
            || ответ.ContentLength is not null
            || ответ.ContentType is not null
            || !context.Request.Path.StartsWithSegments("/api", StringComparison.Ordinal))
        {
            return;
        }

        (string Code, string Message)? конверт = ответ.StatusCode switch
        {
            StatusCodes.Status404NotFound => (ErrorCodes.NotFound, "Адрес не найден"),
            StatusCodes.Status405MethodNotAllowed => (ErrorCodes.MethodNotAllowed, "Этот метод адрес не поддерживает"),
            StatusCodes.Status415UnsupportedMediaType => (ErrorCodes.Validation, "Тип содержимого не поддерживается"),
            StatusCodes.Status413PayloadTooLarge => (ErrorCodes.PayloadTooLarge, "Запрос больше разрешённого"),
            _ => null,
        };

        if (конверт is { } что)
        {
            await WriteAsync(
                context,
                (HttpStatusCode)ответ.StatusCode,
                new ErrorResponse(что.Code, что.Message, Details: null, RequestId(context)),
                context.RequestAborted);
        }
    }

    private static string RequestId(HttpContext context) =>
        context.Items.TryGetValue(RequestIdMiddleware.ItemKey, out var value) && value is string id
            ? id
            : string.Empty;

    private static async Task WriteAsync(
        HttpContext context, HttpStatusCode status, ErrorResponse body, CancellationToken cancellationToken)
    {
        if (context.Response.HasStarted)
        {
            context.Abort();

            return;
        }

        context.Response.Clear();
        context.Response.Headers[RequestIdMiddleware.HeaderName] = body.RequestId;
        context.Response.StatusCode = (int)status;
        context.Response.ContentType = "application/json; charset=utf-8";
        await context.Response.WriteAsync(JsonSerializer.Serialize(body, Json), cancellationToken);
    }
}
