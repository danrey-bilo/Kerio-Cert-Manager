using System.Buffers.Text;
using System.Globalization;
using System.Text;
using Modules.Core.Errors;

namespace Modules.Audit.Reading;

/// <summary>Курсор постраничного обхода: пара «момент и номер строки».</summary>
internal readonly record struct AuditCursor(DateTimeOffset Ts, long Id)
{
    private const char Separator = '\0';

    /// <summary>Собрать курсор для строки.</summary>
    public static string Encode(DateTimeOffset ts, long id) =>
        Base64Url.EncodeToString(Encoding.UTF8.GetBytes(
            $"{ts.ToString("O", CultureInfo.InvariantCulture)}{Separator}{id}"));

    /// <summary>Разобрать курсор.</summary>
    public static AuditCursor? Decode(string? raw)
    {
        if (string.IsNullOrEmpty(raw))
        {
            return null;
        }

        try
        {
            var text = Encoding.UTF8.GetString(Base64Url.DecodeFromChars(raw));
            var separator = text.IndexOf(Separator, StringComparison.Ordinal);

            if (separator <= 0)
            {
                throw AppException.InvalidCursor();
            }

            var ts = DateTimeOffset.ParseExact(
                text[..separator], "O", CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
            var id = long.Parse(text[(separator + 1)..], CultureInfo.InvariantCulture);

            // ⚠ К UTC на входе (ревью 2026-09-15, ts-offset-1).
            return new AuditCursor(ts.ToUniversalTime(), id);
        }
        catch (Exception error) when (error is FormatException or ArgumentException or OverflowException)
        {
            throw AppException.InvalidCursor();
        }
    }
}
