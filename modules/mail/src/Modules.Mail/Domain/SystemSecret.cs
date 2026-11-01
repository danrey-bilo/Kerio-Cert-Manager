namespace Modules.Mail.Domain;

/// <summary>Секрет установки, запечатанный конвертом.</summary>
internal sealed class SystemSecret
{
    /// <summary>Имя из закрытого набора.</summary>
    public required string Key { get; set; }

    /// <summary>Запечатанное значение.</summary>
    public required byte[] Sealed { get; set; }

    /// <summary>Ключ данных, обёрнутый мастер-ключом.</summary>
    public required byte[] DataKey { get; set; }

    /// <summary>Когда правили в последний раз.</summary>
    public DateTimeOffset UpdatedAt { get; set; }

    /// <summary>Кто правил.</summary>
    public Guid? UpdatedBy { get; set; }
}
