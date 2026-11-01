using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Modules.Identity.TwoFactor;

/// <summary>Одноразовые коды по времени: RFC 6238 поверх RFC 4226.</summary>
internal static class Totp
{
    /// <summary>Длина шага в секундах.</summary>
    public const int StepSeconds = 30;

    /// <summary>Сколько цифр в коде.</summary>
    public const int Digits = 6;

    /// <summary>Сколько шагов в обе стороны прощаем за расхождение часов.</summary>
    public const int Window = 1;

    /// <summary>Длина секрета.</summary>
    public const int SecretBytes = 20;

    /// <summary>Завести новый секрет.</summary>
    public static byte[] NewSecret() => RandomNumberGenerator.GetBytes(SecretBytes);

    /// <summary>Номер шага для момента времени.</summary>
    public static long StepAt(DateTimeOffset moment) => moment.ToUnixTimeSeconds() / StepSeconds;

    /// <summary>Код для конкретного шага.</summary>
    public static string Code(ReadOnlySpan<byte> secret, long step, int digits = Digits)
    {
        Span<byte> counter = stackalloc byte[8];
        BinaryPrimitives.WriteInt64BigEndian(counter, step);

        Span<byte> hash = stackalloc byte[HMACSHA1.HashSizeInBytes];
        HMACSHA1.HashData(secret, counter, hash);

        var offset = hash[^1] & 0x0F;

        var binary = ((hash[offset] & 0x7F) << 24)
            | ((hash[offset + 1] & 0xFF) << 16)
            | ((hash[offset + 2] & 0xFF) << 8)
            | (hash[offset + 3] & 0xFF);

        var modulo = (int)Math.Pow(10, digits);

        return (binary % modulo).ToString(CultureInfo.InvariantCulture).PadLeft(digits, '0');
    }

    /// <summary>Проверить код и сказать, какому шагу он соответствует.</summary>
    public static long? Verify(ReadOnlySpan<byte> secret, string code, DateTimeOffset now, long after = -1)
    {
        if (string.IsNullOrWhiteSpace(code))
        {
            return null;
        }

        var trimmed = code.Trim().Replace(" ", string.Empty, StringComparison.Ordinal);

        if (trimmed.Length != Digits)
        {
            return null;
        }

        var current = StepAt(now);

        for (var step = current - Window; step <= current + Window; step++)
        {
            if (step <= after)
            {
                continue;
            }

            if (CryptographicOperations.FixedTimeEquals(
                Encoding.ASCII.GetBytes(Code(secret, step)),
                Encoding.ASCII.GetBytes(trimmed)))
            {
                return step;
            }
        }

        return null;
    }

    /// <summary>Секрет в base32 — так его ждут приложения.</summary>
    public static string ToBase32(ReadOnlySpan<byte> secret)
    {
        const string Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";

        var builder = new StringBuilder((secret.Length * 8 + 4) / 5);
        var buffer = 0;
        var bits = 0;

        foreach (var value in secret)
        {
            buffer = (buffer << 8) | value;
            bits += 8;

            while (bits >= 5)
            {
                builder.Append(Alphabet[(buffer >> (bits - 5)) & 0x1F]);
                bits -= 5;
            }
        }

        if (bits > 0)
        {
            builder.Append(Alphabet[(buffer << (5 - bits)) & 0x1F]);
        }

        return builder.ToString();
    }

    /// <summary>Ссылка <c>otpauth://</c> для QR-кода.</summary>
    public static string Uri(string issuer, string account, ReadOnlySpan<byte> secret)
    {
        var label = System.Uri.EscapeDataString($"{issuer}:{account}");

        return $"otpauth://totp/{label}"
            + $"?secret={ToBase32(secret)}"
            + $"&issuer={System.Uri.EscapeDataString(issuer)}"
            + $"&algorithm=SHA1&digits={Digits}&period={StepSeconds}";
    }
}
