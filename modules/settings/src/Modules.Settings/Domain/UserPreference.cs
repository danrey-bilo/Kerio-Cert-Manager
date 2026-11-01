namespace Modules.Settings.Domain;

/// <summary>Личное предпочтение одного человека.</summary>
internal sealed class UserPreference
{
    /// <summary>Чьё предпочтение.</summary>
    public required Guid UserId { get; set; }

    /// <summary>Имя настройки.</summary>
    public required string Key { get; set; }

    /// <summary>Значение в виде JSON.</summary>
    public required string ValueJson { get; set; }

    /// <summary>Когда правили в последний раз.</summary>
    public DateTimeOffset UpdatedAt { get; set; }
}
