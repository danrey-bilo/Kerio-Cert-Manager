using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace Modules.Crypto;

/// <summary>Конверт секретов на AES-256-GCM.</summary>
public sealed class SecretEnvelope(MasterKeyRing ring) : ISecretEnvelope
{
    /// <summary>Версия формата.</summary>
    private const byte Format = 1;

    /// <summary>Длина ключа данных: AES-256.</summary>
    private const int DataKeyBytes = 32;

    /// <summary>Длина nonce.</summary>
    private const int NonceBytes = 12;

    /// <summary>Длина тега проверки подлинности.</summary>
    private const int TagBytes = 16;

    /// <summary>Заголовок обёртки: формат, версия мастер-ключа, поколение ключа данных.</summary>
    private const int WrapHeaderBytes = 1 + 4 + 4;

    /// <summary>Заголовок секрета: формат и поколение ключа данных.</summary>
    private const int SealHeaderBytes = 1 + 4;

    /// <summary>Домен обёртки.</summary>
    internal static readonly byte[] WrapDomain = Encoding.UTF8.GetBytes("eams:dek:v1\0");

    /// <summary>Домен секрета.</summary>
    internal static readonly byte[] SealDomain = Encoding.UTF8.GetBytes("eams:secret:v1\0");

    public WrappedDataKey CreateDataKey(int generation = 1)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(generation, 1);

        Span<byte> dataKey = stackalloc byte[DataKeyBytes];

        try
        {
            RandomNumberGenerator.Fill(dataKey);

            return Wrap(dataKey, generation);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(dataKey);
        }
    }

    public SealedSecret Seal(WrappedDataKey key, ReadOnlySpan<byte> plaintext, string context)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(context);

        Span<byte> dataKey = stackalloc byte[DataKeyBytes];

        try
        {
            var generation = Unwrap(key, dataKey);

            var blob = new byte[SealHeaderBytes + NonceBytes + TagBytes + plaintext.Length];

            blob[0] = Format;
            BinaryPrimitives.WriteInt32BigEndian(blob.AsSpan(1, 4), generation);

            var nonce = blob.AsSpan(SealHeaderBytes, NonceBytes);
            var tag = blob.AsSpan(SealHeaderBytes + NonceBytes, TagBytes);
            var ciphertext = blob.AsSpan(SealHeaderBytes + NonceBytes + TagBytes);

            // ⚠ Случайный nonce на КАЖДОЕ запечатывание.
            RandomNumberGenerator.Fill(nonce);

            using var aes = new AesGcm(dataKey, TagBytes);

            aes.Encrypt(nonce, plaintext, ciphertext, tag, Associated(SealDomain, blob.AsSpan(0, SealHeaderBytes), context));

            return new SealedSecret(blob);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(dataKey);
        }
    }

    public byte[] Open(WrappedDataKey key, SealedSecret secret, string context)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(context);

        var blob = Require(secret.Bytes, SealHeaderBytes + NonceBytes + TagBytes, "запечатанный секрет");

        if (blob[0] != Format)
        {
            throw new CryptographicException(
                $"Запечатанный секрет формата {blob[0]}, а этот код понимает только {Format}.");
        }

        var generation = BinaryPrimitives.ReadInt32BigEndian(blob.AsSpan(1, 4));

        Span<byte> dataKey = stackalloc byte[DataKeyBytes];

        try
        {
            var keyGeneration = Unwrap(key, dataKey);

            if (keyGeneration != generation)
            {
                throw new CryptographicException(
                    $"Секрет запечатан поколением ключа {generation}, а предъявлено {keyGeneration}. "
                    + "Так выглядит секрет, не перешифрованный после перевыпуска ключа хранилища.");
            }

            var nonce = blob.AsSpan(SealHeaderBytes, NonceBytes);
            var tag = blob.AsSpan(SealHeaderBytes + NonceBytes, TagBytes);
            var ciphertext = blob.AsSpan(SealHeaderBytes + NonceBytes + TagBytes);

            var plaintext = new byte[ciphertext.Length];

            using var aes = new AesGcm(dataKey, TagBytes);

            aes.Decrypt(nonce, ciphertext, tag, plaintext, Associated(SealDomain, blob.AsSpan(0, SealHeaderBytes), context));

            return plaintext;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(dataKey);
        }
    }

    public SealedSecret SealText(WrappedDataKey key, string plaintext, string context)
    {
        ArgumentNullException.ThrowIfNull(plaintext);

        var bytes = Encoding.UTF8.GetBytes(plaintext);

        try
        {
            return Seal(key, bytes, context);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    public string OpenText(WrappedDataKey key, SealedSecret secret, string context)
    {
        var bytes = Open(key, secret, context);

        try
        {
            return Encoding.UTF8.GetString(bytes);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    public bool NeedsRewrap(WrappedDataKey key) => KekVersion(key) != ring.CurrentVersion;

    public WrappedDataKey Rewrap(WrappedDataKey key)
    {
        Span<byte> dataKey = stackalloc byte[DataKeyBytes];

        try
        {
            var generation = Unwrap(key, dataKey);

            return Wrap(dataKey, generation);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(dataKey);
        }
    }

    /// <summary>Какой версией мастер-ключа обёрнут ключ данных.</summary>
    private static int KekVersion(WrappedDataKey key)
    {
        var blob = RequireWrapped(key);

        if (blob[0] != Format)
        {
            throw new CryptographicException(
                $"Обёрнутый ключ формата {blob[0]}, а этот код понимает только {Format}.");
        }

        return BinaryPrimitives.ReadInt32BigEndian(blob.AsSpan(1, 4));
    }

    /// <summary>Обернуть ключ данных действующим мастер-ключом.</summary>
    private WrappedDataKey Wrap(ReadOnlySpan<byte> dataKey, int generation)
    {
        var blob = new byte[WrapHeaderBytes + NonceBytes + TagBytes + DataKeyBytes];

        blob[0] = Format;
        BinaryPrimitives.WriteInt32BigEndian(blob.AsSpan(1, 4), ring.CurrentVersion);
        BinaryPrimitives.WriteInt32BigEndian(blob.AsSpan(5, 4), generation);

        var nonce = blob.AsSpan(WrapHeaderBytes, NonceBytes);
        var tag = blob.AsSpan(WrapHeaderBytes + NonceBytes, TagBytes);
        var ciphertext = blob.AsSpan(WrapHeaderBytes + NonceBytes + TagBytes);

        RandomNumberGenerator.Fill(nonce);

        using var aes = new AesGcm(ring.Key(ring.CurrentVersion), TagBytes);

        aes.Encrypt(nonce, dataKey, ciphertext, tag, Associated(WrapDomain, blob.AsSpan(0, WrapHeaderBytes), null));

        return new WrappedDataKey(blob);
    }

    /// <inheritdoc />
    public (byte[] Sealed, int KeyVersion) SealForDevice(
        WrappedDataKey key, DeviceSealTarget target, Guid vaultId)
    {
        ArgumentNullException.ThrowIfNull(target);

        // ⚠ Открытый ключ данных живёт ровно до `finally` и в вызывающий код не уходит.
        Span<byte> dataKey = stackalloc byte[DataKeyBytes];

        try
        {
            var generation = Unwrap(key, dataKey);

            return (DeviceSeal.Seal(target, vaultId, generation, dataKey), generation);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(dataKey);
        }
    }

    /// <summary>Развернуть ключ данных в переданный буфер.</summary>
    private int Unwrap(WrappedDataKey key, Span<byte> destination)
    {
        var blob = RequireWrapped(key);

        if (blob[0] != Format)
        {
            throw new CryptographicException(
                $"Обёрнутый ключ формата {blob[0]}, а этот код понимает только {Format}.");
        }

        var version = BinaryPrimitives.ReadInt32BigEndian(blob.AsSpan(1, 4));
        var generation = BinaryPrimitives.ReadInt32BigEndian(blob.AsSpan(5, 4));

        var nonce = blob.AsSpan(WrapHeaderBytes, NonceBytes);
        var tag = blob.AsSpan(WrapHeaderBytes + NonceBytes, TagBytes);
        var ciphertext = blob.AsSpan(WrapHeaderBytes + NonceBytes + TagBytes);

        using var aes = new AesGcm(ring.Key(version), TagBytes);

        try
        {
            aes.Decrypt(
                nonce, ciphertext, tag, destination, Associated(WrapDomain, blob.AsSpan(0, WrapHeaderBytes), null));
        }
        catch (AuthenticationTagMismatchException error)
        {
            // ⚠ ⚠ ВНЯТНЫЙ ОТКАЗ ВМЕСТО «не сошёлся тег», и цена разницы измерена.
            throw new CryptographicException(
                $"Обёртка ключа хранилища не расшифровывается мастер-ключом версии {version}. "
                + "Так выглядит установка, поднятая с ДРУГИМ мастер-ключом: проверьте "
                + $"мастер-ключ{ring.HintSuffix} и связку ключей. Если ключ заведомо верный — данные "
                + "испорчены, и это разговор с безопасностью, а не перезапуск.",
                error);
        }

        return generation;
    }

    /// <summary>Связанные данные: домен, заголовок и — для секрета — привязка к месту.</summary>
    internal static byte[] Associated(byte[] domain, ReadOnlySpan<byte> header, string? context)
    {
        var contextBytes = context is null ? 0 : Encoding.UTF8.GetByteCount(context);
        var associated = new byte[domain.Length + header.Length + contextBytes];

        domain.CopyTo(associated.AsSpan());
        header.CopyTo(associated.AsSpan(domain.Length));

        if (context is not null)
        {
            Encoding.UTF8.GetBytes(context, associated.AsSpan(domain.Length + header.Length));
        }

        return associated;
    }

    /// <summary>Проверить, что значение вообще похоже на то, чем себя называет.</summary>
    private static byte[] RequireWrapped(WrappedDataKey key)
    {
        const int Длина = WrapHeaderBytes + NonceBytes + TagBytes + DataKeyBytes;

        var blob = Require(key.Bytes, Длина, "обёрнутый ключ данных");

        if (blob.Length != Длина)
        {
            throw new CryptographicException($"Обёрнутый ключ данных длиной {blob.Length} байт, ожидалось {Длина}.");
        }

        return blob;
    }

    private static byte[] Require(byte[]? blob, int minimum, string what)
    {
        if (blob is null || blob.Length < minimum)
        {
            throw new CryptographicException(
                $"Ожидался {what} длиной от {minimum} байт, получено {blob?.Length ?? 0}.");
        }

        return blob;
    }
}
