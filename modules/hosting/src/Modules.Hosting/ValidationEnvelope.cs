using Modules.Core.Errors;
using Microsoft.AspNetCore.Mvc;

namespace Modules.Hosting;

/// <summary>Ошибки разбора и валидации — тем же конвертом, что и всё остальное.</summary>
public static class ValidationEnvelope
{
    /// <summary>Подключить свой ответ на ошибки разбора.</summary>
    public static IMvcBuilder UseErrorEnvelope(this IMvcBuilder mvc)
    {
        mvc.ConfigureApiBehaviorOptions(options =>
        {
            options.InvalidModelStateResponseFactory = context =>
            {
                var fields = context.ModelState
                    .Where(entry => entry.Value?.Errors.Count > 0)
                    .ToDictionary(
                        entry => Name(entry.Key),
                        entry => (object?)entry.Value!.Errors
                            .Select(error => error.ErrorMessage)
                            .Where(message => message.Length > 0)
                            .ToList());

                var body = new ErrorResponse(
                    ErrorCodes.Validation,
                    "Запрос не прошёл проверку",
                    fields,
                    context.HttpContext.Items.TryGetValue(RequestIdMiddleware.ItemKey, out var id)
                        ? id as string ?? string.Empty
                        : string.Empty);

                return new ObjectResult(body) { StatusCode = StatusCodes.Status400BadRequest };
            };
        });

        return mvc;
    }

    /// <summary>Имя поля в том виде, в каком его прислали.</summary>
    private static string Name(string key) =>
        key.StartsWith("$.", StringComparison.Ordinal) ? key[2..] : key;
}
