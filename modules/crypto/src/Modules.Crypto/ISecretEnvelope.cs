namespace Modules.Crypto;

/// <summary>Ключ данных, обёрнутый мастер-ключом.</summary>
public readonly struct WrappedDataKey(byte[] bytes)
{
    /// <summary>Байты для хранения.</summary>
    public byte[] Bytes { get; } = bytes;

    /// <summary>Пустой ли.</summary>
    public bool IsEmpty => Bytes is null or { Length: 0 };

    /// <summary>Содержимое не печатается: привычка важнее того, что внутри шифротекст.</summary>
    public override string ToString() => "<обёрнутый ключ данных>";
}

/// <summary>Запечатанный секрет: шифротекст вместе со всем, что нужно его открыть, кроме ключа.</summary>
public readonly struct SealedSecret(byte[] bytes)
{
    /// <summary>Байты для хранения.</summary>
    public byte[] Bytes { get; } = bytes;

    /// <summary>Пустой ли.</summary>
    public bool IsEmpty => Bytes is null or { Length: 0 };

    /// <summary>Содержимое не печатается.</summary>
    public override string ToString() => "<запечатанный секрет>";
}

/// <summary>Конверт секретов: мастер-ключ оборачивает ключи данных, ключи данных шифруют секреты.</summary>
public interface ISecretEnvelope
{
    /// <summary>Завести новый ключ данных, уже обёрнутый действующим мастер-ключом.</summary>
    WrappedDataKey CreateDataKey(int generation = 1);

    /// <summary>Запечатать секрет ключом данных.</summary>
    SealedSecret Seal(WrappedDataKey key, ReadOnlySpan<byte> plaintext, string context);

    /// <summary>Открыть секрет.</summary>
    byte[] Open(WrappedDataKey key, SealedSecret secret, string context);

    /// <summary>Запечатать текст.</summary>
    SealedSecret SealText(WrappedDataKey key, string plaintext, string context);

    /// <summary>Открыть текст.</summary>
    string OpenText(WrappedDataKey key, SealedSecret secret, string context);

    /// <summary>Обёрнут ли ключ не действующим мастер-ключом — то есть ждёт ли ротации.</summary>
    bool NeedsRewrap(WrappedDataKey key);

    /// <summary>Перевернуть обёртку на действующий мастер-ключ.</summary>
    WrappedDataKey Rewrap(WrappedDataKey key);

    /// <summary>Запечатать ключ данных НА УСТРОЙСТВО — не разворачивая его наружу.</summary>
    (byte[] Sealed, int KeyVersion) SealForDevice(
        WrappedDataKey key, DeviceSealTarget target, Guid vaultId);
}
