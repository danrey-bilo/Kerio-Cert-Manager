namespace Modules.Settings.Domain;

/// <summary>Заданное значение системной настройки: одно на установку.</summary>
internal sealed class SystemSetting
{
    /// <summary>Имя настройки.</summary>
    public required string Key { get; set; }

    /// <summary>Значение в виде JSON.</summary>
    public required string ValueJson { get; set; }

    /// <summary>Кто правил в последний раз.</summary>
    public Guid? UpdatedBy { get; set; }

    /// <summary>Когда правили в последний раз.</summary>
    public DateTimeOffset UpdatedAt { get; set; }
}
