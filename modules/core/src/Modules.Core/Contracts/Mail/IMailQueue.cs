namespace Modules.Core.Contracts.Mail;

/// <summary>Чем кончилась постановка письма в очередь.</summary>
public enum MailQueueResult
{
    /// <summary>Письмо в очереди.</summary>
    Queued,

    /// <summary>Почта не настроена: слать некуда, и это не ошибка вызывающего.</summary>
    NotConfigured,

    /// <summary>Предел частоты на этот адрес и вид исчерпан.</summary>
    RateLimited,
}

/// <summary>Поставить письмо в очередь.</summary>
public interface IMailQueue
{
    /// <summary>Настроена ли почта настолько, чтобы письмо имело шанс уйти.</summary>
    Task<bool> CanSendAsync(CancellationToken cancellationToken = default);

    /// <summary>Поставить письмо в очередь.</summary>
    Task<MailQueueResult> EnqueueAsync(
        string toAddress,
        string kind,
        string locale,
        IReadOnlyDictionary<string, string> values,
        DateTimeOffset? expiresAt = null,
        CancellationToken cancellationToken = default);

    /// <summary>Поставить письмо с вложениями в очередь и узнать его номер.</summary>
    async Task<MailTicket> EnqueueAsync(
        string toAddress,
        string kind,
        string locale,
        IReadOnlyDictionary<string, string> values,
        IReadOnlyList<MailAttachment> attachments,
        DateTimeOffset? expiresAt = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(attachments);

        if (attachments.Count > 0)
        {
            throw new NotSupportedException("Эта очередь писем не умеет вложений (решение 0120).");
        }

        return new MailTicket(await EnqueueAsync(toAddress, kind, locale, values, expiresAt, cancellationToken), null);
    }

    /// <summary>Поставить письмо с таблицами и вложениями в очередь и узнать его номер.</summary>
    Task<MailTicket> EnqueueAsync(
        string toAddress,
        string kind,
        string locale,
        IReadOnlyDictionary<string, string> values,
        IReadOnlyDictionary<string, MailTable> tables,
        IReadOnlyList<MailAttachment> attachments,
        DateTimeOffset? expiresAt = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(tables);

        return tables.Count > 0
            ? throw new NotSupportedException("Эта очередь писем не умеет таблиц (решение 0121).")
            : EnqueueAsync(toAddress, kind, locale, values, attachments, expiresAt, cancellationToken);
    }

    /// <summary>Что стало с письмами: ждут, ушли или не уйдут.</summary>
    Task<IReadOnlyDictionary<Guid, MailLetterStatus>> StatusAsync(
        IReadOnlyCollection<Guid> ids, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(ids);

        return ids.Count == 0
            ? Task.FromResult<IReadOnlyDictionary<Guid, MailLetterStatus>>(new Dictionary<Guid, MailLetterStatus>())
            : throw new NotSupportedException("Эта очередь писем не знает номеров писем (решение 0120).");
    }

    /// <summary>Отменить письма, которые ещё не ушли: стереть вложения и больше не пытаться.</summary>
    Task<int> CancelAsync(
        IReadOnlyCollection<Guid> ids, string reason, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(ids);

        return ids.Count == 0
            ? Task.FromResult(0)
            : throw new NotSupportedException("Эта очередь писем не умеет отмены (решение 0120).");
    }
}

/// <summary>Итог постановки письма: результат и номер в очереди, если письмо легло и очередь его знает.</summary>
public readonly record struct MailTicket(MailQueueResult Result, Guid? Id);

/// <summary>Состояние письма в очереди.</summary>
public enum MailLetterState
{
    /// <summary>Ещё пробуем.</summary>
    Pending,

    /// <summary>Сервер принял письмо.</summary>
    Sent,

    /// <summary>Не уйдёт: попытки исчерпаны, срок годности истёк или письмо отменено.</summary>
    Dead,
}

/// <summary>Письмо в очереди: состояние, адрес и причина последней неудачи.</summary>
public sealed record MailLetterStatus(MailLetterState State, string ToAddress, string? LastError);

/// <summary>Один файл письма.</summary>
public sealed record MailAttachment(string FileName, string ContentType, byte[] Content)
{
    /// <summary>Содержимое не печатается: во вложении бывает закрытый ключ.</summary>
    public override string ToString() => $"{FileName} ({ContentType}, {Content.Length} байт)";
}

/// <summary>Таблица письма: заголовки колонок и строки ячеек — простым текстом (решение 0121).</summary>
public sealed record MailTable(IReadOnlyList<string> Columns, IReadOnlyList<IReadOnlyList<string>> Rows)
{
    /// <summary>Заголовки колонок.</summary>
    public IReadOnlyList<string> Columns { get; } = Колонки(Columns);

    /// <summary>Строки; в каждой — ячейки в порядке колонок.</summary>
    public IReadOnlyList<IReadOnlyList<string>> Rows { get; } = Строки(Columns, Rows);

    private static string[] Колонки(IReadOnlyList<string> columns)
    {
        ArgumentNullException.ThrowIfNull(columns);

        if (columns.Count == 0)
        {
            throw new ArgumentException("У таблицы письма нет ни одной колонки.", nameof(columns));
        }

        if (columns.Any(колонка => колонка is null))
        {
            throw new ArgumentException("У колонки таблицы письма нет заголовка.", nameof(columns));
        }

        return [.. columns];
    }

    private static IReadOnlyList<string>[] Строки(IReadOnlyList<string> columns, IReadOnlyList<IReadOnlyList<string>> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);

        var колонок = columns?.Count ?? 0;
        var строки = new IReadOnlyList<string>[rows.Count];

        for (var i = 0; i < rows.Count; i++)
        {
            var строка = rows[i] ?? throw new ArgumentException($"Строка {i + 1} таблицы письма пуста (null).", nameof(rows));

            if (строка.Count != колонок)
            {
                throw new ArgumentException(
                    $"В строке {i + 1} таблицы письма ячеек {строка.Count}, а колонок {колонок}.", nameof(rows));
            }

            if (строка.Any(ячейка => ячейка is null))
            {
                throw new ArgumentException($"В строке {i + 1} таблицы письма есть ячейка null.", nameof(rows));
            }

            строки[i] = [.. строка];
        }

        return строки;
    }
}
