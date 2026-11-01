using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Modules.Core.Contracts.Mail;
using Modules.Crypto;
using MimeKit;

namespace Modules.Mail.Sending;

/// <summary>Вложения письма в очереди: запечатать при постановке, открыть при отправке.</summary>
internal static class MailAttachments
{
    /// <summary>Контекст AEAD вложений.</summary>
    internal const string Контекст = "eams:mail-attachments";

    /// <summary>Тип, если присланный не разбирается.</summary>
    internal const string ЗапасныйТип = "application/octet-stream";

    /// <summary>Имя, если от присланного после отрезания пути ничего не осталось.</summary>
    internal const string ЗапасноеИмя = "attachment.bin";

    /// <summary>Значение колонки у письма, чьи вложения были и стёрты.</summary>
    internal static readonly byte[] Стёрто = [];

    /// <summary>Есть ли у письма вложения, ещё не стёртые.</summary>
    internal static bool Ждут(byte[]? sealedAttachments) => sealedAttachments is { Length: > 0 };

    /// <summary>Стёрты ли у письма вложения.</summary>
    internal static bool Стёрты(byte[]? sealedAttachments) => sealedAttachments is { Length: 0 };

    /// <summary>Запечатать вложения.</summary>
    public static byte[]? Seal(
        ISecretEnvelope envelope, WrappedDataKey key, IReadOnlyList<MailAttachment> attachments)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        ArgumentNullException.ThrowIfNull(attachments);

        if (attachments.Count == 0)
        {
            return null;
        }

        var записи = attachments
            .Select(файл =>
            {
                ArgumentNullException.ThrowIfNull(файл, nameof(attachments));

                return new Запись(
                    Имя(файл.FileName),
                    Тип(файл.ContentType),
                    файл.Content ?? throw new ArgumentException("Вложение без содержимого.", nameof(attachments)));
            })
            .ToList();

        var открыто = JsonSerializer.SerializeToUtf8Bytes(записи);

        try
        {
            return envelope.Seal(key, открыто, Контекст).Bytes;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(открыто);
        }
    }

    /// <summary>Открыть вложения письма.</summary>
    public static IReadOnlyList<MailAttachment> Open(
        ISecretEnvelope envelope, WrappedDataKey key, byte[]? sealedAttachments)
    {
        ArgumentNullException.ThrowIfNull(envelope);

        if (!Ждут(sealedAttachments))
        {
            return [];
        }

        var открыто = envelope.Open(key, new SealedSecret(sealedAttachments!), Контекст);

        try
        {
            var записи = JsonSerializer.Deserialize<List<Запись>>(открыто) ?? [];

            return [.. записи.Select(з => new MailAttachment(з.Name, з.Type, з.Content))];
        }
        finally
        {
            CryptographicOperations.ZeroMemory(открыто);
        }
    }

    /// <summary>Имя файла без пути.</summary>
    internal static string Имя(string? fileName)
    {
        var имя = Path.GetFileName((fileName ?? string.Empty).Replace('\\', '/')).Trim();

        return string.IsNullOrEmpty(имя) ? ЗапасноеИмя : имя;
    }

    /// <summary>Тип содержимого или запасной, если присланный не разбирается.</summary>
    internal static string Тип(string? contentType) =>
        contentType is not null && ContentType.TryParse(contentType, out var тип) ? тип.MimeType : ЗапасныйТип;

    /// <summary>Одно вложение в запечатанном массиве.</summary>
    private sealed record Запись(
        [property: JsonPropertyName("name")] string Name,
        [property: JsonPropertyName("type")] string Type,
        [property: JsonPropertyName("content")] byte[] Content);
}
