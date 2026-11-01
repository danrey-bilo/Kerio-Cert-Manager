using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Modules.Crypto;

/// <summary>Разобранное доказательство: чем оно себя называет, до проверки подписи.</summary>
public sealed record DeviceProofClaim(Guid DeviceId, int KeyVersion, long UnixSeconds, string Nonce, byte[] Mac);

/// <summary>Доказательство того, что запрос сделан машиной, на которой родился ключ устройства.</summary>
public static class DeviceProof
{
    /// <summary>Имя заголовка.</summary>
    public const string Header = "X-Eams-Device-Proof";

    /// <summary>Метка версии формата.</summary>
    public const string Version = "v1";

    /// <summary>Метка подписываемой строки.</summary>
    public const string Label = "eams-device-proof:v1";

    /// <summary>Длина соли в байтах.</summary>
    public const int NonceBytes = 16;

    /// <summary>Наибольшее расхождение часов.</summary>
    public static readonly TimeSpan Window = TimeSpan.FromMinutes(2);

    /// <summary>Собрать доказательство.</summary>
    public static string Issue(
        Guid deviceId,
        int keyVersion,
        ReadOnlySpan<byte> proofKey,
        string method,
        string path,
        DateTimeOffset now,
        ReadOnlySpan<byte> body,
        string? nonce = null)
    {
        var соль = nonce ?? Base64Url.Encode(RandomNumberGenerator.GetBytes(NonceBytes));
        var секунды = now.ToUnixTimeSeconds();
        var подпись = Подписать(
            proofKey, deviceId, keyVersion, method, path, секунды, соль, SHA256.HashData(body));

        return string.Join(
            '.',
            Version,
            deviceId.ToString("D"),
            keyVersion.ToString(CultureInfo.InvariantCulture),
            секунды.ToString(CultureInfo.InvariantCulture),
            соль,
            Base64Url.Encode(подпись));
    }

    /// <summary>Разобрать заголовок.</summary>
    public static bool TryParse(string? header, out DeviceProofClaim? claim)
    {
        claim = null;

        if (string.IsNullOrWhiteSpace(header))
        {
            return false;
        }

        var части = header.Split('.');

        if (части.Length != 6 || !string.Equals(части[0], Version, StringComparison.Ordinal))
        {
            return false;
        }

        if (!Guid.TryParseExact(части[1], "D", out var устройство)
            || !int.TryParse(части[2], NumberStyles.None, CultureInfo.InvariantCulture, out var версия)
            || !long.TryParse(части[3], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var секунды)
            || части[4].Length == 0
            || !Base64Url.TryDecode(части[5], out var подпись)
            || подпись.Length != SHA256.HashSizeInBytes)
        {
            return false;
        }

        claim = new DeviceProofClaim(устройство, версия, секунды, части[4], подпись);

        return true;
    }

    /// <summary>Наименьшее время, которое представимо <see cref="DateTimeOffset"/>.</summary>
    private static readonly long МинСекунд = DateTimeOffset.MinValue.ToUnixTimeSeconds();

    /// <summary>Наибольшее время, которое представимо <see cref="DateTimeOffset"/>.</summary>
    private static readonly long МаксСекунд = DateTimeOffset.MaxValue.ToUnixTimeSeconds();

    /// <summary>Свежее ли доказательство по часам сервера.</summary>
    public static bool Свежее(DeviceProofClaim claim, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(claim);

        // ⚠ Время вне представимого — не свежее, а не пятисотка (решение 0110, находка 26).
        if (claim.UnixSeconds < МинСекунд || claim.UnixSeconds > МаксСекунд)
        {
            return false;
        }

        var когда = DateTimeOffset.FromUnixTimeSeconds(claim.UnixSeconds);

        return (now - когда).Duration() <= Window;
    }

    /// <summary>Сходится ли подпись.</summary>
    public static bool Сходится(
        DeviceProofClaim claim,
        ReadOnlySpan<byte> proofKey,
        string method,
        string path,
        ReadOnlySpan<byte> body) =>
        СходитсяПоХешу(claim, proofKey, method, path, SHA256.HashData(body));

    /// <summary>То же, но тело уже посчитано хешем.</summary>
    public static bool СходитсяПоХешу(
        DeviceProofClaim claim,
        ReadOnlySpan<byte> proofKey,
        string method,
        string path,
        ReadOnlySpan<byte> bodyHash)
    {
        ArgumentNullException.ThrowIfNull(claim);

        var наша = Подписать(
            proofKey, claim.DeviceId, claim.KeyVersion, method, path, claim.UnixSeconds, claim.Nonce, bodyHash);

        return CryptographicOperations.FixedTimeEquals(наша, claim.Mac);
    }

    /// <summary>Подписываемая строка.</summary>
    private static byte[] Подписать(
        ReadOnlySpan<byte> proofKey,
        Guid deviceId,
        int keyVersion,
        string method,
        string path,
        long секунды,
        string соль,
        ReadOnlySpan<byte> bodyHash)
    {
        var строка = string.Join(
            '\n',
            Label,
            deviceId.ToString("D"),
            keyVersion.ToString(CultureInfo.InvariantCulture),
            (method ?? string.Empty).ToUpperInvariant(),
            path ?? string.Empty,
            секунды.ToString(CultureInfo.InvariantCulture),
            соль,
            Base64Url.Encode(bodyHash));

        return HMACSHA256.HashData(proofKey, Encoding.UTF8.GetBytes(строка));
    }
}
