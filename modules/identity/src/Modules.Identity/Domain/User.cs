namespace Modules.Identity.Domain;

/// <summary>Учётная запись человека.</summary>
internal sealed class User
{
    /// <summary>Идентификатор.</summary>
    public Guid Id { get; set; }

    /// <summary>Адрес, он же логин.</summary>
    public string Email { get; set; } = string.Empty;

    /// <summary>Как показывать человека.</summary>
    public string? DisplayName { get; set; }

    /// <summary>Хеш пароля.</summary>
    public string PasswordHash { get; set; } = string.Empty;

    /// <summary>Может ли входить.</summary>
    public bool IsActive { get; set; } = true;

    /// <summary>Проходит любую проверку прав.</summary>
    public bool IsSuperuser { get; set; }

    /// <summary>Пора сменить пароль: выданный администратором или просроченный.</summary>
    public bool MustChangePassword { get; set; }

    /// <summary>Когда пароль сменили в последний раз.</summary>
    public DateTimeOffset? PasswordChangedAt { get; set; }

    /// <summary>Сколько неудачных входов подряд.</summary>
    public int FailedLoginCount { get; set; }

    /// <summary>Отдел — свободной строкой; не задан — <c>null</c> (решение 0110).</summary>
    public string? Department { get; set; }

    /// <summary>До какого момента вход заблокирован перебором.</summary>
    public DateTimeOffset? LockedUntil { get; set; }

    /// <summary>Сколько блокировок подряд — следующая длится вдвое дольше (решение 0119).</summary>
    public int LockoutLevel { get; set; }

    /// <summary>Когда человек активировал учётную запись.</summary>
    public DateTimeOffset? ActivatedAt { get; set; }

    /// <summary>Когда владелец впервые подтвердил второй фактор.</summary>
    public DateTimeOffset? TwoFactorConfiguredAt { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    /// <summary>Мягкое удаление.</summary>
    public DateTimeOffset? DeletedAt { get; set; }

    /// <summary>Чем этот человек подтверждает вход.</summary>
    public string? SecondFactorMethod { get; set; }
}
