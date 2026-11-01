using System.Text.Json.Serialization;
using Modules.Identity.Domain;

namespace Modules.Identity.Api;

/// <summary>Учётная запись, как её видит клиент.</summary>
internal sealed record UserOut(
    [property: JsonPropertyName("id")] Guid Id,
    [property: JsonPropertyName("email")] string Email,
    [property: JsonPropertyName("display_name")] string? DisplayName,
    [property: JsonPropertyName("is_active")] bool IsActive,
    [property: JsonPropertyName("is_superuser")] bool IsSuperuser,
    [property: JsonPropertyName("must_change_password")] bool MustChangePassword,
    [property: JsonPropertyName("activated_at")] DateTimeOffset? ActivatedAt,
    [property: JsonPropertyName("locked_until")] DateTimeOffset? LockedUntil,
    [property: JsonPropertyName("created_at")] DateTimeOffset CreatedAt,
    [property: JsonPropertyName("updated_at")] DateTimeOffset UpdatedAt,
    [property: JsonPropertyName("roles")] IReadOnlyList<UserRoleOut>? Roles = null,
    [property: JsonPropertyName("authenticator_enabled")] bool AuthenticatorEnabled = false,
    [property: JsonPropertyName("department")] string? Department = null)
{
    /// <summary>Из читающей проекции (вскрытие 27.7): хеш пароля из базы не выходит.</summary>
    public static UserOut From(Storage.UserRow user) => new(
        user.Id,
        user.Email,
        user.DisplayName,
        user.IsActive,
        user.IsSuperuser,
        user.MustChangePassword,
        user.ActivatedAt,
        user.LockedUntil,
        user.CreatedAt,
        user.UpdatedAt,
        Department: user.Department);

    /// <summary>Из сущности — там, где она уже на руках после записи.</summary>
    public static UserOut From(User user) => new(
        user.Id,
        user.Email,
        user.DisplayName,
        user.IsActive,
        user.IsSuperuser,
        user.MustChangePassword,
        user.ActivatedAt,
        user.LockedUntil,
        user.CreatedAt,
        user.UpdatedAt,
        Department: user.Department);
}

/// <summary>Название назначенной роли, без состава прав.</summary>
internal sealed record UserRoleOut(
    [property: JsonPropertyName("id")] Guid Id,
    [property: JsonPropertyName("name")] string Name);

/// <summary>Страница списка пользователей.</summary>
internal sealed record UsersPageOut(
    [property: JsonPropertyName("items")] IReadOnlyList<UserOut> Items,
    [property: JsonPropertyName("next_cursor")] string? NextCursor);
