using System.Text.Json.Serialization;

namespace Modules.Identity.Api;

/// <summary>Ответ на заведение: сам человек и ссылка приглашения.</summary>
internal sealed record UserCreatedOut(
    [property: JsonPropertyName("user")] UserOut User,
    [property: JsonPropertyName("invitation_token")] string? InvitationToken,
    [property: JsonPropertyName("invitation_expires_at")] DateTimeOffset? InvitationExpiresAt)
{
    /// <summary>Печать без секретов: запись попадает в лог, в отладчик и в текст исключения.</summary>
    private bool PrintMembers(System.Text.StringBuilder builder)
    {
        builder.Append("секреты скрыты: InvitationToken");

        return true;
    }
}
