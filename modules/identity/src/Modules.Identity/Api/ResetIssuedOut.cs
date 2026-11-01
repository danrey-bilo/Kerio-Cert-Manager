using System.Text.Json.Serialization;

namespace Modules.Identity.Api;

/// <summary>Выданная ссылка сброса пароля.</summary>
internal sealed record ResetIssuedOut(
    [property: JsonPropertyName("reset_token")] string? ResetToken,
    [property: JsonPropertyName("expires_at")] DateTimeOffset ExpiresAt)
{
    /// <summary>Печать без секретов: запись попадает в лог, в отладчик и в текст исключения.</summary>
    private bool PrintMembers(System.Text.StringBuilder builder)
    {
        builder.Append("секреты скрыты: ResetToken");

        return true;
    }
}
