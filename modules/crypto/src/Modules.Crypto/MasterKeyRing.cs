using System.Globalization;
using System.Security.Cryptography;

namespace Modules.Crypto;

/// <summary>Мастер-ключи установки: действующий и прежние.</summary>
public sealed class MasterKeyRing
{
    /// <summary>Длина мастер-ключа.</summary>
    public const int KeyBytes = 32;

    private readonly Dictionary<int, byte[]> _keys;

    private MasterKeyRing(Dictionary<int, byte[]> keys, int currentVersion, string? hint)
    {
        _keys = keys;
        CurrentVersion = currentVersion;
        Hint = string.IsNullOrWhiteSpace(hint) ? null : hint.Trim();
    }

    /// <summary>Версия, которой оборачиваем новое.</summary>
    public int CurrentVersion { get; }

    /// <summary>Подсказка проекта оператору: откуда приходит ключ и чем его создают.</summary>
    public string? Hint { get; }

    /// <summary>Подсказка в скобках для текста отказа — или ничего.</summary>
    internal string HintSuffix => Suffix(Hint);

    /// <summary>Сколько версий в ринге.</summary>
    public int Count => _keys.Count;

    /// <summary>Разобрать содержимое файла секрета.</summary>
    public static MasterKeyRing Parse(string content, string? hint = null)
    {
        ArgumentNullException.ThrowIfNull(content);

        var keys = new Dictionary<int, byte[]>();
        var lines = content.Split('\n');

        try
        {
            for (var index = 0; index < lines.Length; index++)
            {
                var line = lines[index].Trim();
                var number = index + 1;

                if (line.Length == 0 || line.StartsWith('#'))
                {
                    continue;
                }

                var (version, encoded) = Split(line, number);

                if (!keys.TryAdd(version, Decode(encoded, number, version)))
                {
                    throw new InvalidOperationException(
                        $"Мастер-ключ версии {version} задан дважды (строка {number}). "
                        + "Какой из них действующий — угадывать нельзя.");
                }
            }
        }
        catch
        {
            // ⚠ Отказ на поздней строке не должен оставлять уже разобранные ключи в куче.
            foreach (var осиротевший in keys.Values)
            {
                System.Security.Cryptography.CryptographicOperations.ZeroMemory(осиротевший);
            }

            throw;
        }

        if (keys.Count == 0)
        {
            // ⚠ Чем создают файл, знает проект, а не модуль (решение 0111): сценарий называет подсказка хоста.
            throw new InvalidOperationException(
                "В источнике мастер-ключа нет ни одного ключа. Пустой файл секрета обычно "
                + "означает, что его забыли создать" + Suffix(hint) + ".");
        }

        return new MasterKeyRing(keys, keys.Keys.Max(), hint);
    }

    /// <summary>Собрать ринг из готовых ключей — для тестов и для ротации в коде.</summary>
    public static MasterKeyRing FromKeys(IReadOnlyDictionary<int, byte[]> keys, string? hint = null)
    {
        ArgumentNullException.ThrowIfNull(keys);

        if (keys.Count == 0)
        {
            throw new ArgumentException("Ринг без ключей бессмыслен", nameof(keys));
        }

        var copy = new Dictionary<int, byte[]>(keys.Count);

        foreach (var (version, key) in keys)
        {
            if (version < 1)
            {
                throw new ArgumentException($"Версия ключа должна быть от 1, а не {version}", nameof(keys));
            }

            if (key.Length != KeyBytes)
            {
                throw new ArgumentException(
                    $"Мастер-ключ версии {version} длиной {key.Length} байт вместо {KeyBytes}", nameof(keys));
            }

            copy[version] = key.ToArray();
        }

        return new MasterKeyRing(copy, copy.Keys.Max(), hint);
    }

    private static string Suffix(string? hint) =>
        string.IsNullOrWhiteSpace(hint) ? string.Empty : $" ({hint.Trim()})";

    /// <summary>Есть ли такая версия.</summary>
    public bool Has(int version) => _keys.ContainsKey(version);

    /// <summary>Ключ нужной версии.</summary>
    internal ReadOnlySpan<byte> Key(int version) =>
        _keys.TryGetValue(version, out var key)
            ? key
            : throw new CryptographicException(
                $"Ключ данных обёрнут мастер-ключом версии {version}, а его в установке нет. "
                + "Так выглядит потерянный при ротации ключ: без него эти данные не прочитать.");

    /// <summary>Ни при каких обстоятельствах не печатает ключи.</summary>
    public override string ToString() =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"MasterKeyRing(версий: {_keys.Count}, действующая: {CurrentVersion})");

    /// <summary>Разобрать строку в пару «версия, ключ».</summary>
    private static (int Version, string Encoded) Split(string line, int number)
    {
        var colon = line.IndexOf(':');

        if (colon < 0)
        {
            return (1, line);
        }

        var prefix = line[..colon];

        if (!int.TryParse(prefix, NumberStyles.None, CultureInfo.InvariantCulture, out var version) || version < 1)
        {
            throw new InvalidOperationException(
                $"Строка {number}: перед двоеточием ожидается номер версии от 1.");
        }

        return (version, line[(colon + 1)..]);
    }

    /// <summary>Раскодировать и проверить длину.</summary>
    private static byte[] Decode(string encoded, int number, int version)
    {
        byte[] key;

        try
        {
            key = Convert.FromBase64String(encoded);
        }
        catch (FormatException)
        {
            throw new InvalidOperationException(
                $"Строка {number}: ключ версии {version} не разбирается как base64.");
        }

        if (key.Length != KeyBytes)
        {
            CryptographicOperations.ZeroMemory(key);

            throw new InvalidOperationException(
                $"Строка {number}: ключ версии {version} длиной {key.Length} байт вместо {KeyBytes}. "
                + "Короче — это молча не AES-256; генератор даёт ровно 32 байта.");
        }

        return key;
    }
}
