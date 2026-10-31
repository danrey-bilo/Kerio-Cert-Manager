namespace KerioCertManager.Modules.KeyManager.Domain;

/// <summary>Состояние связи с Key Manager Plus: одна строка на установку.</summary>
internal sealed class KeyManagerState
{
    public const short SingletonId = 1;

    public short Id { get; set; } = SingletonId;

    /// <summary>Ключ API (<c>AUTHTOKEN</c>), запечатанный; <c>null</c> — не задан.</summary>
    public byte[]? ApiKey { get; set; }

    /// <summary>Ключ данных, обёрнутый мастер-ключом.</summary>
    public byte[]? DataKey { get; set; }

    public DateTimeOffset? UpdatedAt { get; set; }

    /// <summary>Без внешнего ключа: <c>users</c> — чужая таблица.</summary>
    public Guid? UpdatedBy { get; set; }

    public DateTimeOffset? LastPollAt { get; set; }

    /// <summary><c>null</c> — последний опрос удался.</summary>
    public string? LastPollError { get; set; }

    public int? LastPollCount { get; set; }
}

/// <summary>Сертификат, замеченный опросом.</summary>
internal sealed class KnownCertificate
{
    public required string CommonName { get; set; }

    public required string SerialNumber { get; set; }

    /// <summary><c>null</c> — KMP не прислал дату или она не разобралась.</summary>
    public DateOnly? ExpiresOn { get; set; }

    /// <summary>Первое появление — момент, когда замечено продление.</summary>
    public DateTimeOffset FirstSeenAt { get; set; }

    public DateTimeOffset LastSeenAt { get; set; }
}
