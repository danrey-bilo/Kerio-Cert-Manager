using System.Text.Json.Serialization;

namespace Modules.Identity.Api;

/// <summary>Требования к паролю — так, как их можно показать человеку до попытки.</summary>
internal sealed record PasswordPolicyOut(
    [property: JsonPropertyName("min_length")] int MinLength,
    [property: JsonPropertyName("max_length_bytes")] int MaxLengthBytes,
    [property: JsonPropertyName("require_uppercase")] bool RequireUppercase,
    [property: JsonPropertyName("require_lowercase")] bool RequireLowercase,
    [property: JsonPropertyName("require_digit")] bool RequireDigit,
    [property: JsonPropertyName("require_symbol")] bool RequireSymbol);
