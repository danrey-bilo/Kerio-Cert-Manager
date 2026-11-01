using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace Modules.Identity.Api;

/// <summary>Что присылают, активируя учётную запись.</summary>
internal sealed record ActivateIn
{
    /// <summary>Печать без секретов: запись попадает в лог, в отладчик и в текст исключения.</summary>
    private bool PrintMembers(System.Text.StringBuilder builder)
    {
        builder.Append("секреты скрыты: Token, Password");

        return true;
    }

    [Required]
    [StringLength(200, MinimumLength = 10)]
    [JsonPropertyName("token")]
    public string Token { get; init; } = string.Empty;

    [Required]
    [StringLength(1024, MinimumLength = 1)]
    [JsonPropertyName("password")]
    public string Password { get; init; } = string.Empty;
}

/// <summary>Ссылка приглашения в теле запроса: секрет из письма не попадает в адрес API (решение 0120).</summary>
internal sealed record ActivationLinkIn
{
    /// <summary>Печать без секретов: запись попадает в лог, в отладчик и в текст исключения.</summary>
    private bool PrintMembers(System.Text.StringBuilder builder)
    {
        builder.Append("секреты скрыты: Token");

        return true;
    }

    /// <summary>Токен ссылки приглашения — тот же, что у <c>GET /api/activation?token=</c>.</summary>
    [Required]
    [StringLength(200, MinimumLength = 10)]
    [JsonPropertyName("token")]
    public string Token { get; init; } = string.Empty;
}
