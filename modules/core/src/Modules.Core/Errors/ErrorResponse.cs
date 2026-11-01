using System.Text.Json.Serialization;

namespace Modules.Core.Errors;

/// <summary>Единственная форма, в которой ошибка уходит наружу.</summary>
public sealed record ErrorResponse(
    [property: JsonPropertyName("code")] string Code,
    [property: JsonPropertyName("message")] string Message,
    [property: JsonPropertyName("details")] IReadOnlyDictionary<string, object?>? Details,
    [property: JsonPropertyName("request_id")] string RequestId);
