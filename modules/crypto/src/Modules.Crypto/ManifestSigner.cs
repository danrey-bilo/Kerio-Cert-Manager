using System.Security.Cryptography;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Crypto.Signers;

namespace Modules.Crypto;

/// <summary>Подпись манифеста офлайн-аренды: Ed25519.</summary>
public sealed class ManifestSigner
{
    /// <summary>Длина seed'а Ed25519.</summary>
    public const int SeedBytes = 32;

    /// <summary>Длина подписи.</summary>
    public const int SignatureBytes = 64;

    private readonly Ed25519PrivateKeyParameters _приватный;

    private ManifestSigner(Ed25519PrivateKeyParameters приватный) => _приватный = приватный;

    /// <summary>Публичная половина — её отдают клиенту и вшивают в сборку.</summary>
    public byte[] PublicKey => _приватный.GeneratePublicKey().GetEncoded();

    /// <summary>Собрать подписывающего из seed'а.</summary>
    public static ManifestSigner FromSeed(ReadOnlySpan<byte> seed)
    {
        if (seed.Length != SeedBytes)
        {
            throw new CryptographicException($"Seed ключа подписи — {SeedBytes} байта.");
        }

        return new ManifestSigner(new Ed25519PrivateKeyParameters(seed.ToArray(), 0));
    }

    /// <summary>Разобрать seed из base64.</summary>
    public static ManifestSigner Parse(string base64)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(base64);

        byte[] seed;

        try
        {
            seed = Convert.FromBase64String(base64.Trim());
        }
        catch (FormatException)
        {
            throw new CryptographicException("Ключ подписи манифеста — base64 из 32 байт.");
        }

        // ⚠ Seed затирается: FromSeed копирует его внутрь, и оригинал секретa установки оставался в куче навсегда (этап 23.5).
        try
        {
            return FromSeed(seed);
        }
        finally
        {
            System.Security.Cryptography.CryptographicOperations.ZeroMemory(seed);
        }
    }

    /// <summary>Подписать канонические байты.</summary>
    public byte[] Sign(ReadOnlySpan<byte> payload)
    {
        var подписант = new Ed25519Signer();

        подписант.Init(true, _приватный);
        подписант.BlockUpdate(payload);

        return подписант.GenerateSignature();
    }

    /// <summary>Проверить подпись публичной половиной.</summary>
    public static bool Verify(ReadOnlySpan<byte> publicKey, ReadOnlySpan<byte> payload, ReadOnlySpan<byte> signature)
    {
        if (publicKey.Length != SeedBytes || signature.Length != SignatureBytes)
        {
            return false;
        }

        var проверяющий = new Ed25519Signer();

        проверяющий.Init(false, new Ed25519PublicKeyParameters(publicKey.ToArray(), 0));
        проверяющий.BlockUpdate(payload);

        return проверяющий.VerifySignature(signature.ToArray());
    }
}
