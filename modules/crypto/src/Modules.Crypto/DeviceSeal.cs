using System.Security.Cryptography;
using System.Text;
using Org.BouncyCastle.Crypto.Agreement;
using Org.BouncyCastle.Crypto.Parameters;

namespace Modules.Crypto;

/// <summary>Кому печатаем: устройство и его публичная половина.</summary>
public sealed record DeviceSealTarget(Guid DeviceId, byte[] PublicKey);

/// <summary>Запечатывание ключа данных на публичный ключ устройства.</summary>
public static class DeviceSeal
{
    /// <summary>Длина ключа X25519 — и публичного, и приватного.</summary>
    public const int KeyBytes = 32;

    /// <summary>Длина nonce AES-GCM.</summary>
    public const int NonceBytes = 12;

    /// <summary>Длина тега AES-GCM.</summary>
    public const int TagBytes = 16;

    /// <summary>Метка выведения ключа.</summary>
    public const string Label = "eams-device-seal:v1";

    /// <summary>Запечатать ключ данных устройству.</summary>
    public static byte[] Seal(
        DeviceSealTarget target, Guid vaultId, int keyVersion, ReadOnlySpan<byte> dataKey)
    {
        ArgumentNullException.ThrowIfNull(target);

        return Запечатать(target, Контекст(target.DeviceId, vaultId, keyVersion), dataKey);
    }

    /// <summary>Запечатать устройству ключ доказательства (решение 0075).</summary>
    public static byte[] SealProof(DeviceSealTarget target, int keyVersion, ReadOnlySpan<byte> proofKey)
    {
        ArgumentNullException.ThrowIfNull(target);

        return Запечатать(target, ПроверочныйКонтекст(target.DeviceId, keyVersion), proofKey);
    }

    /// <summary>Запечатать устройству значение секрета для подключения (решение 0098).</summary>
    public static byte[] SealLaunch(
        DeviceSealTarget target, Guid credentialId, string part, ReadOnlySpan<byte> value)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentException.ThrowIfNullOrWhiteSpace(part);

        if (value.IsEmpty)
        {
            throw new ArgumentException("Пустое значение не печатается.", nameof(value));
        }

        return Запечатать(target, ЗапусковыйКонтекст(target.DeviceId, credentialId, part), value);
    }

    private static byte[] Запечатать(DeviceSealTarget target, byte[] aad, ReadOnlySpan<byte> dataKey)
    {
        ПроверитьКлюч(target.PublicKey);

        // ⚠ Эфемерная пара — на каждый вызов.
        var seed = new byte[KeyBytes];
        RandomNumberGenerator.Fill(seed);

        byte[]? ключ = null;

        try
        {
            var эфемерный = new X25519PrivateKeyParameters(seed, 0);
            var эфемерныйПубличный = эфемерный.GeneratePublicKey().GetEncoded();

            var общий = Согласовать(эфемерный, target.PublicKey);

            try
            {
                // ⚠ Выведенный AES-ключ — тоже в finally (этап 23.5).
                ключ = Вывести(общий, эфемерныйПубличный, target.PublicKey);

                var nonce = new byte[NonceBytes];

                RandomNumberGenerator.Fill(nonce);

                var шифртекст = new byte[dataKey.Length];
                var тег = new byte[TagBytes];

                using var gcm = new AesGcm(ключ, TagBytes);

                gcm.Encrypt(nonce, dataKey, шифртекст, тег, aad);

                var итог = new byte[эфемерныйПубличный.Length + NonceBytes + шифртекст.Length + TagBytes];

                эфемерныйПубличный.CopyTo(итог, 0);
                nonce.CopyTo(итог, эфемерныйПубличный.Length);
                шифртекст.CopyTo(итог, эфемерныйПубличный.Length + NonceBytes);
                тег.CopyTo(итог, эфемерныйПубличный.Length + NonceBytes + шифртекст.Length);

                return итог;
            }
            finally
            {
                CryptographicOperations.ZeroMemory(общий);
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(seed);

            if (ключ is not null)
            {
                CryptographicOperations.ZeroMemory(ключ);
            }
        }
    }

    /// <summary>Развернуть запечатанное приватной половиной устройства.</summary>
    public static byte[] Unseal(
        ReadOnlySpan<byte> sealed_, ReadOnlySpan<byte> devicePrivateKey, Guid deviceId, Guid vaultId, int keyVersion) =>
        Развернуть(sealed_, devicePrivateKey, Контекст(deviceId, vaultId, keyVersion));

    /// <summary>Развернуть запечатанный ключ доказательства.</summary>
    public static byte[] UnsealProof(
        ReadOnlySpan<byte> sealed_, ReadOnlySpan<byte> devicePrivateKey, Guid deviceId, int keyVersion) =>
        Развернуть(sealed_, devicePrivateKey, ПроверочныйКонтекст(deviceId, keyVersion));

    /// <summary>Развернуть значение для подключения.</summary>
    public static byte[] UnsealLaunch(
        ReadOnlySpan<byte> sealed_, ReadOnlySpan<byte> devicePrivateKey, Guid deviceId, Guid credentialId, string part) =>
        Развернуть(sealed_, devicePrivateKey, ЗапусковыйКонтекст(deviceId, credentialId, part));

    private static byte[] Развернуть(
        ReadOnlySpan<byte> sealed_, ReadOnlySpan<byte> devicePrivateKey, byte[] aad)
    {
        if (devicePrivateKey.Length != KeyBytes)
        {
            throw new CryptographicException("Приватный ключ устройства — 32 байта.");
        }

        if (sealed_.Length <= KeyBytes + NonceBytes + TagBytes)
        {
            throw new CryptographicException("Запечатанное короче минимально возможного.");
        }

        var эфемерныйПубличный = sealed_[..KeyBytes].ToArray();
        var nonce = sealed_.Slice(KeyBytes, NonceBytes);
        var тело = sealed_[(KeyBytes + NonceBytes)..];
        var шифртекст = тело[..^TagBytes];
        var тег = тело[^TagBytes..];

        // ⚠ Копия приватной половины и выведенный ключ затираются в finally (этап 23.5).
        var копияПриватного = devicePrivateKey.ToArray();
        byte[]? ключ = null;

        try
        {
            var приватный = new X25519PrivateKeyParameters(копияПриватного, 0);
            var общий = Согласовать(приватный, эфемерныйПубличный);

            try
            {
                ключ = Вывести(общий, эфемерныйПубличный, приватный.GeneratePublicKey().GetEncoded());

                var открытое = new byte[шифртекст.Length];

                using var gcm = new AesGcm(ключ, TagBytes);

                gcm.Decrypt(nonce, шифртекст, тег, открытое, aad);

                return открытое;
            }
            finally
            {
                CryptographicOperations.ZeroMemory(общий);
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(копияПриватного);

            if (ключ is not null)
            {
                CryptographicOperations.ZeroMemory(ключ);
            }
        }
    }

    /// <summary>Строка AAD.</summary>
    internal static byte[] Контекст(Guid deviceId, Guid vaultId, int keyVersion) =>
        Encoding.UTF8.GetBytes($"device:{deviceId:D}:vault:{vaultId:D}:v{keyVersion}");

    /// <summary>Строка AAD для ключа доказательства.</summary>
    internal static byte[] ПроверочныйКонтекст(Guid deviceId, int keyVersion) =>
        Encoding.UTF8.GetBytes($"device:{deviceId:D}:proof:v{keyVersion}");

    /// <summary>Строка AAD значения для подключения.</summary>
    internal static byte[] ЗапусковыйКонтекст(Guid deviceId, Guid credentialId, string part) =>
        Encoding.UTF8.GetBytes($"device:{deviceId:D}:launch:{credentialId:D}:{part}");

    /// <summary>Общий секрет X25519.</summary>
    private static byte[] Согласовать(X25519PrivateKeyParameters приватный, byte[] чужойПубличный)
    {
        var общий = new byte[KeyBytes];
        var соглашение = new X25519Agreement();

        соглашение.Init(приватный);

        try
        {
            соглашение.CalculateAgreement(new X25519PublicKeyParameters(чужойПубличный, 0), общий, 0);
        }
        catch (Exception ошибка) when (ошибка is InvalidOperationException or ArgumentException)
        {
            throw new CryptographicException("Публичный ключ устройства не годится для согласования.");
        }

        if (общий.All(байт => байт == 0))
        {
            CryptographicOperations.ZeroMemory(общий);

            throw new CryptographicException("Общий секрет вырожден: низкопорядковая точка.");
        }

        return общий;
    }

    /// <summary>HKDF-SHA256 с пустой солью.</summary>
    private static byte[] Вывести(byte[] общий, byte[] эфемерныйПубличный, byte[] публичныйУстройства)
    {
        var info = new byte[Encoding.UTF8.GetByteCount(Label) + эфемерныйПубличный.Length + публичныйУстройства.Length];
        var меткаДлина = Encoding.UTF8.GetBytes(Label, info);

        эфемерныйПубличный.CopyTo(info, меткаДлина);
        публичныйУстройства.CopyTo(info, меткаДлина + эфемерныйПубличный.Length);

        return HKDF.DeriveKey(HashAlgorithmName.SHA256, общий, KeyBytes, salt: null, info);
    }

    private static void ПроверитьКлюч(byte[] публичный)
    {
        ArgumentNullException.ThrowIfNull(публичный);

        if (публичный.Length != KeyBytes)
        {
            throw new CryptographicException($"Публичный ключ устройства — {KeyBytes} байта.");
        }
    }
}
