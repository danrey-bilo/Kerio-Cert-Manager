using System.IO.Compression;
using System.Security.Cryptography;
using System.Security.Cryptography.Pkcs;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.RegularExpressions;

namespace KerioCertManager.Modules.KeyManager.Source;

/// <summary>Сертификаты в DER из файла Key Manager Plus: PEM, DER, PKCS#7 (P7B) или ZIP с ними.</summary>
internal static partial class CertificateFiles
{
    private const int ЗаписейZip = 32;

    private const int ПотолокЗаписи = 2 * 1024 * 1024;

    private const int ПотолокАрхива = 8 * 1024 * 1024;

    /// <summary>Все сертификаты файла в DER, без повторов, в порядке появления.</summary>
    public static IReadOnlyList<byte[]> Ders(byte[] file)
    {
        ArgumentNullException.ThrowIfNull(file);

        var найдено = new List<byte[]>();
        Собрать(file, найдено, глубина: 0);

        return [.. найдено.DistinctBy(Convert.ToHexString)];
    }

    private static void Собрать(byte[] файл, List<byte[]> найдено, int глубина)
    {
        if (файл.Length == 0 || глубина > 2)
        {
            return;
        }

        if (файл.AsSpan().StartsWith("PK\x03\x04"u8))
        {
            ИзZip(файл, найдено, глубина);
            return;
        }

        var текст = Encoding.ASCII.GetString(файл);

        if (текст.Contains("-----BEGIN", StringComparison.Ordinal))
        {
            foreach (Match блок in БлокPem().Matches(текст))
            {
                var вид = блок.Groups[1].Value;
                byte[] der;

                try
                {
                    der = Convert.FromBase64String(string.Concat(блок.Groups[2].Value.Where(с => !char.IsWhiteSpace(с))));
                }
                catch (FormatException)
                {
                    continue;
                }

                if (вид is "CERTIFICATE" or "X509 CERTIFICATE" or "TRUSTED CERTIFICATE")
                {
                    if (Сертификат(der))
                    {
                        найдено.Add(der);
                    }
                }
                else if (вид is "PKCS7" or "CMS")
                {
                    ИзPkcs7(der, найдено);
                }
            }

            return;
        }

        if (файл[0] == 0x30)
        {
            if (Сертификат(файл))
            {
                найдено.Add(файл);
            }
            else
            {
                ИзPkcs7(файл, найдено);
            }
        }
    }

    private static bool Сертификат(byte[] der)
    {
        try
        {
            using var _ = X509CertificateLoader.LoadCertificate(der);
            return true;
        }
        catch (CryptographicException)
        {
            return false;
        }
    }

    private static void ИзPkcs7(byte[] der, List<byte[]> найдено)
    {
        try
        {
            var cms = new SignedCms();
            cms.Decode(der);

            foreach (var сертификат in cms.Certificates)
            {
                найдено.Add(сертификат.RawData);
            }
        }
        catch (CryptographicException)
        {
        }
    }

    private static void ИзZip(byte[] файл, List<byte[]> найдено, int глубина)
    {
        try
        {
            using var архив = new ZipArchive(new MemoryStream(файл, writable: false), ZipArchiveMode.Read);
            var прочитано = 0L;

            foreach (var запись in архив.Entries.Take(ЗаписейZip))
            {
                if (запись.Length is <= 0 or > ПотолокЗаписи || запись.FullName.EndsWith(".key", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                прочитано += запись.Length;

                if (прочитано > ПотолокАрхива)
                {
                    break;
                }

                using var поток = запись.Open();
                using var буфер = new MemoryStream();
                поток.CopyTo(буфер);

                Собрать(буфер.ToArray(), найдено, глубина + 1);
            }
        }
        catch (InvalidDataException)
        {
        }
    }

    [GeneratedRegex("-----BEGIN ([A-Z0-9 ]+)-----(.*?)-----END \\1-----", RegexOptions.Singleline)]
    private static partial Regex БлокPem();
}
