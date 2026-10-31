using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.RegularExpressions;

namespace KerioCertManager.Modules.Kerio.Certificates;

/// <summary>Отпечатки, серийные номера и имена сертификатов в одном виде.</summary>
internal static partial class Fingerprints
{
    /// <summary>SHA-256 сертификата (DER) — hex заглавными без разделителей.</summary>
    public static string Sha256(byte[] der)
    {
        ArgumentNullException.ThrowIfNull(der);

        return Convert.ToHexString(SHA256.HashData(der));
    }

    /// <summary>SHA-256 сертификата.</summary>
    public static string Sha256(X509Certificate2 certificate)
    {
        ArgumentNullException.ThrowIfNull(certificate);

        return Sha256(certificate.RawData);
    }

    /// <summary>Отпечаток из ввода человека (<c>AA:BB…</c>, пробелы, строчные) в hex заглавными; негодный — <c>null</c>.</summary>
    public static string? NormalizePin(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var hex = string.Concat(text.Where(с => !char.IsWhiteSpace(с) && с != ':' && с != '-')).ToUpperInvariant();

        return hex.Length == 64 && hex.All(Uri.IsHexDigit) ? hex : null;
    }

    /// <summary>Отпечаток для людей: пары через двоеточие.</summary>
    public static string Pretty(string? sha256) =>
        string.IsNullOrEmpty(sha256) ? string.Empty : string.Join(':', sha256.Chunk(2).Select(п => new string(п)));

    /// <summary>Серийный номер в одном виде: только hex, без разделителей и ведущих нулей, строчными.</summary>
    public static string Serial(string? serial)
    {
        if (string.IsNullOrEmpty(serial))
        {
            return string.Empty;
        }

        Span<char> буфер = serial.Length <= 256 ? stackalloc char[serial.Length] : new char[serial.Length];
        var длина = 0;

        foreach (var c in serial)
        {
            if (Uri.IsHexDigit(c) && (длина > 0 || c != '0'))
            {
                буфер[длина++] = char.ToLowerInvariant(c);
            }
        }

        return длина == 0 ? string.Empty : new string(буфер[..длина]);
    }

    /// <summary>Один и тот же серийный номер, как бы его ни записали.</summary>
    public static bool SameSerial(string? а, string? б) =>
        !string.IsNullOrEmpty(Serial(а)) && string.Equals(Serial(а), Serial(б), StringComparison.Ordinal);

    /// <summary>Common Name субъекта или издателя; нет — всё отличительное имя.</summary>
    public static string CommonName(X500DistinguishedName name)
    {
        ArgumentNullException.ThrowIfNull(name);

        var cn = name.EnumerateRelativeDistinguishedNames()
            .Where(rdn => rdn.GetSingleElementType().Value == "2.5.4.3")
            .Select(rdn => rdn.GetSingleElementValue())
            .FirstOrDefault(значение => !string.IsNullOrWhiteSpace(значение));

        return cn ?? name.Name;
    }

    /// <summary>DNS-имена сертификата из SubjectAltName.</summary>
    public static IReadOnlyList<string> DnsNames(X509Certificate2 certificate)
    {
        ArgumentNullException.ThrowIfNull(certificate);

        var san = certificate.Extensions.OfType<X509SubjectAlternativeNameExtension>().FirstOrDefault();

        return san is null ? [] : [.. san.EnumerateDnsNames()];
    }

    /// <summary>Покрывает ли сертификат имя <paramref name="name"/> (точно или звёздочкой), в том числе имя‑звёздочку.</summary>
    public static bool Covers(X509Certificate2 certificate, string name)
    {
        ArgumentNullException.ThrowIfNull(certificate);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        var искомое = name.Trim().TrimEnd('.').ToLowerInvariant();
        var имена = DnsNames(certificate).Append(CommonName(certificate.SubjectName))
            .Select(и => и.Trim().TrimEnd('.').ToLowerInvariant())
            .ToList();

        if (имена.Contains(искомое, StringComparer.Ordinal))
        {
            return true;
        }

        return !искомое.StartsWith("*.", StringComparison.Ordinal) && имена.Any(и => ПодЗвёздочку(и, искомое));
    }

    private static bool ПодЗвёздочку(string шаблон, string имя)
    {
        if (!шаблон.StartsWith("*.", StringComparison.Ordinal))
        {
            return false;
        }

        var хвост = шаблон[1..];
        var точка = имя.IndexOf('.', StringComparison.Ordinal);

        return точка > 0 && string.Equals(имя[точка..], хвост, StringComparison.Ordinal);
    }

    /// <summary>Сертификаты в PEM одним текстом.</summary>
    public static string Pem(IEnumerable<X509Certificate2> certificates)
    {
        ArgumentNullException.ThrowIfNull(certificates);

        var текст = new StringBuilder();

        foreach (var сертификат in certificates)
        {
            текст.Append(сертификат.ExportCertificatePem()).Append('\n');
        }

        return текст.ToString();
    }

    /// <summary>Первый сертификат из текста PEM; нет — <c>null</c>.</summary>
    public static X509Certificate2? FirstFromPem(string? pem)
    {
        if (string.IsNullOrWhiteSpace(pem))
        {
            return null;
        }

        var блок = БлокСертификата().Match(pem);

        if (!блок.Success)
        {
            return null;
        }

        try
        {
            var der = Convert.FromBase64String(string.Concat(блок.Groups[1].Value.Where(с => !char.IsWhiteSpace(с))));

            return X509CertificateLoader.LoadCertificate(der);
        }
        catch (Exception ошибка) when (ошибка is FormatException or CryptographicException)
        {
            return null;
        }
    }

    [GeneratedRegex("-----BEGIN CERTIFICATE-----(.*?)-----END CERTIFICATE-----", RegexOptions.Singleline)]
    private static partial Regex БлокСертификата();
}
