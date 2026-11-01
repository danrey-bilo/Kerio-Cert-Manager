using System.Buffers.Text;
using System.Text;
using Modules.Core.Errors;

namespace Modules.Identity.Storage;

/// <summary>Курсор постраничного обхода списка людей: адрес последней показанной строки.</summary>
internal static class UserCursor
{
    /// <summary>Собрать курсор по последней строке страницы.</summary>
    public static string Encode(string email) => Base64Url.EncodeToString(Encoding.UTF8.GetBytes(email));

    /// <summary>Разобрать курсор.</summary>
    public static string? Decode(string? raw)
    {
        if (string.IsNullOrEmpty(raw))
        {
            return null;
        }

        try
        {
            var email = Encoding.UTF8.GetString(Base64Url.DecodeFromChars(raw));

            return email.Length == 0 ? throw AppException.InvalidCursor() : email;
        }
        catch (Exception error) when (error is FormatException or ArgumentException)
        {
            throw AppException.InvalidCursor();
        }
    }
}
