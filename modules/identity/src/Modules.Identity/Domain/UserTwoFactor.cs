namespace Modules.Identity.Domain;

/// <summary>Второй фактор человека: запечатанный секрет и память о принятом коде.</summary>
internal sealed class UserTwoFactor
{
    /// <summary>Чей.</summary>
    public Guid UserId { get; set; }

    /// <summary>Ключ данных этой строки, обёрнутый мастер-ключом.</summary>
    public byte[] DataKey { get; set; } = [];

    /// <summary>Запечатанный seed.</summary>
    public byte[] Secret { get; set; } = [];

    /// <summary>Когда завели.</summary>
    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>Когда подтвердили кодом.</summary>
    public DateTimeOffset? ConfirmedAt { get; set; }

    /// <summary>Шаг последнего принятого кода.</summary>
    public long LastStep { get; set; } = -1;
}
