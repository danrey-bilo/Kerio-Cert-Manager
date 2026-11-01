using System.Globalization;

namespace Modules.Identity.Passwords;

/// <summary>Разобранный хеш пароля: параметры, соль и сам хеш — всё, что лежит в колонке.</summary>
internal sealed record PasswordHash(Argon2Profile Profile, byte[] Salt, byte[] Hash);

/// <summary>Формат PHC — то, как хеш пароля живёт в базе.</summary>
internal static class PhcString
{
    /// <summary>Идентификатор варианта.</summary>
    private const string Algorithm = "argon2id";

    /// <summary>Версия самого argon2 — 0x13, она же 19.</summary>
    private const int Version = 19;

    /// <summary>Собрать строку для колонки.</summary>
    public static string Encode(PasswordHash hash)
    {
        var profile = hash.Profile;

        return string.Create(
            CultureInfo.InvariantCulture,
            $"${Algorithm}$v={Version}$m={profile.MemoryKib},t={profile.Iterations},p={profile.Parallelism}"
            + $"${Base64NoPadding(hash.Salt)}${Base64NoPadding(hash.Hash)}");
    }

    /// <summary>Разобрать строку из колонки.</summary>
    public static bool TryDecode(string? encoded, out PasswordHash? hash)
    {
        hash = null;

        if (string.IsNullOrEmpty(encoded))
        {
            return false;
        }

        var parts = encoded.Split('$');

        if (parts.Length != 6 || parts[0].Length != 0 || parts[1] != Algorithm)
        {
            return false;
        }

        if (parts[2] != $"v={Version}")
        {
            return false;
        }

        if (!TryParameters(parts[3], out var memory, out var iterations, out var parallelism))
        {
            return false;
        }

        if (!TryBase64(parts[4], out var salt) || !TryBase64(parts[5], out var raw))
        {
            return false;
        }

        var profile = new Argon2Profile(memory, iterations, parallelism, salt.Length, raw.Length);

        try
        {
            profile.Validate();
        }
        catch (ArgumentOutOfRangeException)
        {
            return false;
        }

        hash = new PasswordHash(profile, salt, raw);

        return true;
    }

    /// <summary>Разобрать <c>m=…,t=…,p=…</c> — строго в этом порядке.</summary>
    private static bool TryParameters(string text, out int memory, out int iterations, out int parallelism)
    {
        memory = iterations = parallelism = 0;

        var parts = text.Split(',');

        return parts.Length == 3
            && TryNamed(parts[0], "m", out memory)
            && TryNamed(parts[1], "t", out iterations)
            && TryNamed(parts[2], "p", out parallelism);
    }

    private static bool TryNamed(string text, string name, out int value)
    {
        value = 0;

        return text.StartsWith(name + '=', StringComparison.Ordinal)
            && int.TryParse(text.AsSpan(name.Length + 1), NumberStyles.None, CultureInfo.InvariantCulture, out value);
    }

    private static string Base64NoPadding(byte[] value) => Convert.ToBase64String(value).TrimEnd('=');

    private static bool TryBase64(string text, out byte[] value)
    {
        value = [];

        var padded = text.PadRight(text.Length + ((4 - (text.Length % 4)) % 4), '=');

        if (!Convert.TryFromBase64String(padded, new byte[padded.Length], out var written))
        {
            return false;
        }

        var buffer = new byte[written];
        Convert.TryFromBase64String(padded, buffer, out _);
        value = buffer;

        return value.Length > 0;
    }
}
