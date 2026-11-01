using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Modules.Crypto;

/// <summary>Подпись значений недоверенных хранилищ на ключе, выведенном из мастер-ключа.</summary>
public sealed class IntegrityProtector(MasterKeyRing ring) : IIntegrityProtector
{
    /// <summary>Длина ключа подписи.</summary>
    private const int KeyBytes = 32;

    /// <summary>Разделитель назначений.</summary>
    private const string Domain = "eams:integrity:v1:";

    /// <summary>Выведенные ключи: по одному на пару «назначение, версия».</summary>
    private readonly ConcurrentDictionary<(string Purpose, int Version), byte[]> _keys = new();

    public string Protect(string purpose, string recordKey, string value)
    {
        ArgumentException.ThrowIfNullOrEmpty(purpose);
        ArgumentNullException.ThrowIfNull(recordKey);
        ArgumentNullException.ThrowIfNull(value);

        var version = ring.CurrentVersion;
        var tag = Sign(Key(purpose, version), recordKey, value);

        return string.Create(
            CultureInfo.InvariantCulture,
            $"{version}.{Base64Url.Encode(tag)}.{value}");
    }

    public string Fingerprint(string purpose, string value)
    {
        ArgumentException.ThrowIfNullOrEmpty(purpose);
        ArgumentNullException.ThrowIfNull(value);

        var version = ring.CurrentVersion;

        // ⚠ Своё назначение у отпечатков.
        var tag = HMACSHA256.HashData(Key(FingerprintPrefix + purpose, version), Encoding.UTF8.GetBytes(value));

        return string.Create(
            CultureInfo.InvariantCulture,
            $"{version}.{Base64Url.Encode(tag.AsSpan(0, FingerprintBytes))}");
    }

    /// <summary>Приставка назначения у ключей отпечатков.</summary>
    private const string FingerprintPrefix = "fingerprint:";

    /// <summary>Сколько байт HMAC в отпечатке: 128 бит — столкновений не будет.</summary>
    private const int FingerprintBytes = 16;

    public IntegrityVerdict TryUnprotect(string purpose, string recordKey, string envelope, out string? value)
    {
        ArgumentException.ThrowIfNullOrEmpty(purpose);
        ArgumentNullException.ThrowIfNull(recordKey);

        value = null;

        if (!Разобрать(envelope, out var version, out var tag, out var payload))
        {
            // ⚠ Именно Tampered, а не Foreign.
            return IntegrityVerdict.Tampered;
        }

        if (!ring.Has(version))
        {
            return IntegrityVerdict.Foreign;
        }

        var ожидаемая = Sign(Key(purpose, version), recordKey, payload);

        // ⚠ Сравнение постоянного времени.
        if (!CryptographicOperations.FixedTimeEquals(tag, ожидаемая))
        {
            return IntegrityVerdict.Tampered;
        }

        value = payload;

        return IntegrityVerdict.Ok;
    }

    /// <summary>Подпись: сначала ключ записи, потом значение, между ними — разделитель.</summary>
    private static byte[] Sign(byte[] key, string recordKey, string value)
    {
        var bytes = new byte[
            Encoding.UTF8.GetByteCount(recordKey) + 1 + Encoding.UTF8.GetByteCount(value)];

        var written = Encoding.UTF8.GetBytes(recordKey, bytes);
        bytes[written] = 0;
        Encoding.UTF8.GetBytes(value, bytes.AsSpan(written + 1));

        return HMACSHA256.HashData(key, bytes);
    }

    /// <summary>Ключ подписи для назначения и версии мастер-ключа.</summary>
    private byte[] Key(string purpose, int version)
    {
        if (_keys.TryGetValue((purpose, version), out var готовый))
        {
            return готовый;
        }

        var ключ = new byte[KeyBytes];

        HKDF.DeriveKey(
            HashAlgorithmName.SHA256,
            ring.Key(version),
            ключ,
            salt: default,
            info: Encoding.UTF8.GetBytes(Domain + purpose));

        return _keys.GetOrAdd((purpose, version), ключ);
    }

    /// <summary>Разобрать конверт.</summary>
    private static bool Разобрать(
        string? envelope, out int version, out byte[] tag, out string payload)
    {
        version = 0;
        tag = [];
        payload = string.Empty;

        if (string.IsNullOrEmpty(envelope))
        {
            return false;
        }

        var первая = envelope.IndexOf('.', StringComparison.Ordinal);

        if (первая <= 0)
        {
            return false;
        }

        var вторая = envelope.IndexOf('.', первая + 1);

        if (вторая < 0)
        {
            return false;
        }

        if (!int.TryParse(
                envelope.AsSpan(0, первая), NumberStyles.None, CultureInfo.InvariantCulture, out version)
            || version < 1)
        {
            return false;
        }

        if (!Base64Url.TryDecode(envelope.AsSpan(первая + 1, вторая - первая - 1), out tag)
            || tag.Length != KeyBytes)
        {
            return false;
        }

        payload = envelope[(вторая + 1)..];

        return true;
    }
}

/// <summary>base64url без выравнивания — тот же алфавит, что у JWT.</summary>
internal static class Base64Url
{
    // ⚠ Штатный кодек из BCL (есть с .NET 9), а не Replace-цепочки.
    public static string Encode(ReadOnlySpan<byte> bytes) =>
        System.Buffers.Text.Base64Url.EncodeToString(bytes);

    public static bool TryDecode(ReadOnlySpan<char> text, out byte[] bytes)
    {
        bytes = [];

        Span<byte> буфер = stackalloc byte[64];

        // ⚠ Перегрузка с OperationStatus, а не Try*.
        var статус = System.Buffers.Text.Base64Url.DecodeFromChars(
            text, буфер, out var прочитано, out var written);

        if (статус != System.Buffers.OperationStatus.Done || прочитано != text.Length)
        {
            return false;
        }

        bytes = буфер[..written].ToArray();

        return true;
    }
}
