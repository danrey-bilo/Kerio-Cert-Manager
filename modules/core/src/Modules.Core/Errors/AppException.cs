using System.Net;

namespace Modules.Core.Errors;

/// <summary>Ошибка, которую можно показать наружу: с машинным кодом, статусом и подробностями.</summary>
public class AppException : Exception
{
    public AppException(
        string code,
        string message,
        HttpStatusCode status = HttpStatusCode.BadRequest,
        IReadOnlyDictionary<string, object?>? details = null)
        : base(message)
    {
        Code = code;
        Status = status;
        Details = details;
    }

    /// <summary>Машинный код — по нему клиент решает, что делать.</summary>
    public string Code { get; }

    /// <summary>Код ответа HTTP.</summary>
    public HttpStatusCode Status { get; }

    /// <summary>Машинные подробности или <c>null</c>.</summary>
    public IReadOnlyDictionary<string, object?>? Details { get; }

    /// <summary>Прав не хватает.</summary>
    public static AppException PermissionDenied(string message = "Недостаточно прав") =>
        new(ErrorCodes.PermissionDenied, message, HttpStatusCode.Forbidden);

    /// <summary>Не найдено — или найдено, но показывать нельзя.</summary>
    public static AppException NotFound(string message = "Не найдено") =>
        new(ErrorCodes.NotFound, message, HttpStatusCode.NotFound);

    /// <summary>Испорченный курсор пагинации.</summary>
    public static AppException InvalidCursor() =>
        new(ErrorCodes.InvalidCursor, "Недействительный курсор", HttpStatusCode.UnprocessableContent);
}
