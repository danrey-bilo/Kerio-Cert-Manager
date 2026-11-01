namespace Modules.Identity.Storage;

/// <summary>Пользователь для ЧТЕНИЯ: только то, что уходит наружу.</summary>
internal sealed record UserRow(
    Guid Id,
    string Email,
    string? DisplayName,
    bool IsActive,
    bool IsSuperuser,
    bool MustChangePassword,
    DateTimeOffset? ActivatedAt,
    DateTimeOffset? LockedUntil,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    string? Department = null);
