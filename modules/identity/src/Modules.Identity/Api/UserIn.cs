using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace Modules.Identity.Api;

/// <summary>Что присылают, заводя учётную запись.</summary>
internal sealed record UserIn
{
    /// <summary>Печать без секретов: запись попадает в лог, в отладчик и в текст исключения.</summary>
    private bool PrintMembers(System.Text.StringBuilder builder)
    {
        builder.Append("секреты скрыты: Password");

        return true;
    }

    [Required]
    [StringLength(320, MinimumLength = 3)]
    // ⚠ Форма, а не только длина (вскрытие 43).
    [EmailAddress]
    [JsonPropertyName("email")]
    public string Email { get; init; } = string.Empty;

    [StringLength(200)]
    [JsonPropertyName("display_name")]
    public string? DisplayName { get; init; }

    /// <summary>Пароль.</summary>
    [StringLength(1024)]
    [JsonPropertyName("password")]
    public string? Password { get; init; }

    [StringLength(Storage.IdentityEntities.DepartmentLength)]
    [JsonPropertyName("department")]
    public string? Department { get; init; }

    [JsonPropertyName("is_superuser")]
    public bool IsSuperuser { get; init; }

    [JsonPropertyName("must_change_password")]
    public bool MustChangePassword { get; init; } = true;

    /// <summary>Роли приглашённого: выдаются в одной транзакции с учётной записью и письмом.</summary>
    [MaxLength(100)]
    [JsonPropertyName("role_ids")]
    public IReadOnlyList<Guid>? RoleIds { get; init; }
}
