namespace Modules.Core.Redaction;

/// <summary>Имена полей, значения которых наружу не выходят никогда.</summary>
public static class SensitiveKeys
{
    /// <summary>Чем заменяется значение.</summary>
    public const string Redacted = "***";

    /// <summary>Базовый список.</summary>
    public static readonly IReadOnlySet<string> Base = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "password",
        "secret",
        "token",
        "authorization",
        "notes",
        "access_token",
        "refresh_token",
        "jwt_secret",
        "master_kek",
        "kek",
        "dek",
        "secret_ciphertext",
        "secret_fields",
        "cookie",
        "set-cookie",
    };

    /// <summary>Секретное ли это имя поля.</summary>
    public static bool IsSensitive(string name, IReadOnlySet<string> keys)
    {
        if (string.IsNullOrEmpty(name) || keys.Contains(name))
        {
            return keys.Contains(name);
        }

        var last = LastWord(name);

        return last.Length > 0 && keys.Contains(last);
    }

    /// <summary>Последнее слово имени: <c>newPassword</c> и <c>new_password</c> дают <c>password</c>.</summary>
    private static string LastWord(string name)
    {
        var start = 0;

        for (var i = 0; i < name.Length; i++)
        {
            var разделитель = name[i] is '_' or '-' or '.';
            var сменаРегистра = i > 0 && char.IsUpper(name[i]) && !char.IsUpper(name[i - 1]);

            if (разделитель)
            {
                start = i + 1;
            }
            else if (сменаРегистра)
            {
                start = i;
            }
        }

        return name[start..];
    }
}

/// <summary>Модуль объявляет, какие его поля секретны.</summary>
public interface ISensitiveKeyProvider
{
    /// <summary>Имена полей этого модуля, значения которых не должны попадать в журнал.</summary>
    IReadOnlyCollection<string> Keys { get; }
}
