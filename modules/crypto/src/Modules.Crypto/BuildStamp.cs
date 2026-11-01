using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Modules.Crypto;

/// <summary>Что сервер удостоверил о сборке клиента.</summary>
public sealed record BuildStampPayload(
    [property: JsonPropertyName("id")] Guid Id,
    [property: JsonPropertyName("v")] string Version,
    [property: JsonPropertyName("u")] string Url,
    [property: JsonPropertyName("t")] DateTimeOffset IssuedAt)
{
    /// <summary>Один и тот же адрес, записанный одинаково.</summary>
    public static string Нормально(string? адрес) =>
        Uri.TryCreate(адрес?.Trim(), UriKind.Absolute, out var разобран)
            ? $"{разобран.Scheme.ToLowerInvariant()}://{разобран.Authority.ToLowerInvariant()}"
                + разобран.AbsolutePath.TrimEnd('/')
            : (адрес?.Trim().TrimEnd('/') ?? string.Empty);
}

/// <summary>Отпечаток сборки: сервер удостоверяет, что этот клиент выпущен им.</summary>
public static class BuildStamp
{
    /// <summary>Разделитель между полезной частью и подписью.</summary>
    private const char Разделитель = '.';

    /// <summary>Дальше этого не разбираем: заголовок с мегабайтом мусора — не отпечаток.</summary>
    public const int MaxChars = 512;

    private static readonly JsonSerializerOptions Формат = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    /// <summary>Собрать отпечаток: подписать полезную часть и склеить.</summary>
    public static string Issue(BuildStampPayload payload, ManifestSigner signer)
    {
        ArgumentNullException.ThrowIfNull(payload);
        ArgumentNullException.ThrowIfNull(signer);

        var тело = JsonSerializer.SerializeToUtf8Bytes(payload, Формат);

        return string.Concat(
            Base64Url(тело),
            Разделитель,
            Base64Url(signer.Sign(тело)));
    }

    /// <summary>Разобрать и проверить подпись.</summary>
    public static BuildStampPayload? Verify(string? stamp, ReadOnlySpan<byte> publicKey)
    {
        if (stamp is not { Length: > 0 and <= MaxChars })
        {
            return null;
        }

        var черта = stamp.IndexOf(Разделитель, StringComparison.Ordinal);

        if (черта <= 0 || черта == stamp.Length - 1)
        {
            return null;
        }

        try
        {
            var тело = FromBase64Url(stamp.AsSpan(0, черта));
            var подпись = FromBase64Url(stamp.AsSpan(черта + 1));

            if (подпись.Length != ManifestSigner.SignatureBytes
                || !ManifestSigner.Verify(publicKey, тело, подпись))
            {
                return null;
            }

            return JsonSerializer.Deserialize<BuildStampPayload>(тело, Формат);
        }
        catch (Exception ошибка) when (ошибка is FormatException or JsonException)
        {
            return null;
        }
    }

    /// <summary>Base64 без набивки и без знаков, требующих экранирования в заголовке.</summary>
    private static string Base64Url(ReadOnlySpan<byte> байты) =>
        Convert.ToBase64String(байты).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static byte[] FromBase64Url(ReadOnlySpan<char> текст)
    {
        var обычный = new StringBuilder(текст.Length + 3);

        foreach (var знак in текст)
        {
            обычный.Append(знак switch { '-' => '+', '_' => '/', var иной => иной });
        }

        // ⚠ Набивку возвращаем: `Convert.FromBase64String` без неё бросает, а длина строки всегда кратна четырём после добавления не более трёх.
        Набивка(обычный);

        return Convert.FromBase64String(обычный.ToString());

        static void Набивка(StringBuilder строка)
        {
            while (строка.Length % 4 != 0)
            {
                строка.Append('=');
            }
        }
    }

    /// <summary>Как записать время в реестре и в журнале — одинаково у всех.</summary>
    public static string Момент(DateTimeOffset когда) =>
        когда.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
}
