using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace Modules.Identity.Api;

/// <summary>Что можно изменить.</summary>
internal sealed record UserPatchIn
{
    [StringLength(200)]
    [JsonPropertyName("display_name")]
    public string? DisplayName { get; init; }

    // ⚠ Поля is_active здесь больше нет.

    /// <summary>Отдел: не передан — не менять, пустая строка — снять.</summary>
    [StringLength(Storage.IdentityEntities.DepartmentLength)]
    [JsonPropertyName("department")]
    public string? Department { get; init; }

    [JsonPropertyName("is_superuser")]
    public bool? IsSuperuser { get; init; }

    [JsonPropertyName("must_change_password")]
    public bool? MustChangePassword { get; init; }
}
