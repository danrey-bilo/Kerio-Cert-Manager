using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;

namespace Modules.Crypto;

/// <summary>Откуда берётся ключ доказательства устройства.</summary>
public interface IDeviceProofKeys
{
    /// <summary>Действующая версия мастер-ключа.</summary>
    int CurrentVersion { get; }

    /// <summary>Есть ли такая версия в кольце.</summary>
    bool Has(int version);

    /// <summary>Ключ доказательства для устройства и версии мастер-ключа.</summary>
    byte[] Derive(Guid deviceId, int version);
}

/// <summary>Ключ, которым устройство доказывает, что оно — это оно.</summary>
public sealed class DeviceProofKeys(MasterKeyRing ring) : IDeviceProofKeys
{
    /// <summary>Длина выведенного ключа.</summary>
    public const int KeyBytes = 32;

    /// <summary>Метка выведения.</summary>
    public const string Label = "eams-device-proof:v1:";

    private readonly ConcurrentDictionary<(Guid, int), byte[]> _ключи = new();

    /// <inheritdoc />
    public int CurrentVersion => ring.CurrentVersion;

    /// <inheritdoc />
    public bool Has(int version) => ring.Has(version);

    /// <inheritdoc />
    public byte[] Derive(Guid deviceId, int version)
    {
        if (_ключи.TryGetValue((deviceId, version), out var готовый))
        {
            return [.. готовый];
        }

        var ключ = new byte[KeyBytes];

        HKDF.DeriveKey(
            HashAlgorithmName.SHA256,
            ring.Key(version),
            ключ,
            salt: default,
            info: Encoding.UTF8.GetBytes(Label + deviceId.ToString("D")));

        return [.. _ключи.GetOrAdd((deviceId, version), ключ)];
    }
}
