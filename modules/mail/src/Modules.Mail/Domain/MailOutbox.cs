namespace Modules.Mail.Domain;

/// <summary>Письмо в очереди на отправку.</summary>
internal sealed class MailOutbox
{
    /// <summary>UUIDv7 из приложения: возрастает во времени, порядок очереди совпадает с ключом.</summary>
    public required Guid Id { get; set; }

    /// <summary>Кому.</summary>
    public required string ToAddress { get; set; }

    /// <summary>Тема.</summary>
    public required string Subject { get; set; }

    /// <summary>Тело письма.</summary>
    public required byte[] SealedBody { get; set; }

    /// <summary>Ключ данных этого письма, обёрнутый мастер-ключом.</summary>
    public required byte[] DataKey { get; set; }

    /// <summary>Тело письма в HTML, запечатанное тем же ключом данных, что текстовое тело (решение 0121).</summary>
    public byte[]? SealedHtml { get; set; }

    /// <summary>Вложения письма, запечатанные тем же ключом данных, что тело (решение 0120).</summary>
    public byte[]? SealedAttachments { get; set; }

    /// <summary>Вид письма: приглашение, сброс, код входа.</summary>
    public required string Kind { get; set; }

    /// <summary>Когда поставлено в очередь.</summary>
    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>Когда пробовать в следующий раз.</summary>
    public DateTimeOffset NextAttemptAt { get; set; }

    /// <summary>Сколько раз пробовали.</summary>
    public int Attempts { get; set; }

    /// <summary>Когда ушло.</summary>
    public DateTimeOffset? SentAt { get; set; }

    /// <summary>Срок годности: не ушло до него — не уйдёт (решение 0110).</summary>
    public DateTimeOffset? ExpiresAt { get; set; }

    /// <summary>Чем кончилась последняя попытка.</summary>
    public string? LastError { get; set; }
}
