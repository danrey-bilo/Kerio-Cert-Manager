using System.Text.Json.Serialization;

namespace Modules.Identity.Api;

/// <summary>Что показывает экран активации до ввода пароля.</summary>
internal sealed record ActivationInfoOut(
    [property: JsonPropertyName("email")] string Email,
    [property: JsonPropertyName("display_name")] string? DisplayName,
    [property: JsonPropertyName("policy")] PasswordPolicyOut Policy);
