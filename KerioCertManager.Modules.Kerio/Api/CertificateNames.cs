using System.Net;
using KerioCertManager.Modules.Kerio.Domain;
using KerioCertManager.Modules.KeyManager.Contracts;
using Modules.Core.Errors;

namespace KerioCertManager.Modules.Kerio.Api;

/// <summary>Проверка списка сертификатов сервера по Key Manager Plus: чего нет — не сохраняется, с причиной и подсказкой.</summary>
internal static class CertificateNames
{
    /// <summary>Поле в ответе об ошибке.</summary>
    public const string Field = "certificates";

    public const int MaxLength = 255;

    /// <summary>Проблема с одним именем.</summary>
    internal sealed record Problem(string Name, string Reason, string? Suggestion);

    public static (string? Primary, IReadOnlyList<string> Extras) Check(
        string? primary, IReadOnlyList<string>? extras, IReadOnlyList<SourceCertificate> kmp, IEnumerable<string> already)
    {
        ArgumentNullException.ThrowIfNull(kmp);
        ArgumentNullException.ThrowIfNull(already);

        var известные = kmp.Select(в => в.CommonName).Distinct(StringComparer.OrdinalIgnoreCase)
            .ToDictionary(и => и, StringComparer.OrdinalIgnoreCase);
        var прежние = already.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var проблемы = new List<Problem>();
        var встречены = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        string? Привести(string? текст, bool обязательно)
        {
            var имя = текст?.Trim();

            if (string.IsNullOrEmpty(имя))
            {
                if (обязательно)
                {
                    проблемы.Add(new Problem(string.Empty, "пустое имя — уберите строку или впишите имя сертификата", null));
                }

                return null;
            }

            if (имя.Length > MaxLength)
            {
                проблемы.Add(new Problem(имя[..40] + "…", $"длиннее {MaxLength} символов", null));
                return null;
            }

            if (имя.Any(char.IsWhiteSpace))
            {
                проблемы.Add(new Problem(имя, "в имени пробел — имя сертификата пишется без пробелов", Ближайшее(имя.Replace(" ", string.Empty, StringComparison.Ordinal), известные.Keys)));
                return null;
            }

            if (!встречены.Add(имя))
            {
                проблемы.Add(new Problem(имя, "указан дважды", null));
                return null;
            }

            if (известные.TryGetValue(имя, out var вKmp))
            {
                return вKmp;
            }

            if (прежние.Contains(имя))
            {
                return имя;
            }

            проблемы.Add(new Problem(
                имя,
                известные.Count == 0
                    ? "Key Manager Plus не отдал ни одного сертификата — проверьте связь с KMP в настройках"
                    : "нет в Key Manager Plus — проверьте написание",
                Ближайшее(имя, известные.Keys)));

            return null;
        }

        var поУмолчанию = Привести(primary, обязательно: false);
        var свои = new List<string>();

        foreach (var имя in extras ?? [])
        {
            if (Привести(имя, обязательно: true) is { } годное)
            {
                свои.Add(годное);
            }
        }

        if ((extras?.Count ?? 0) > KerioServer.MaxExtras)
        {
            проблемы.Add(new Problem(string.Empty, $"сертификатов по именам больше {KerioServer.MaxExtras}", null));
        }

        if (проблемы.Count > 0)
        {
            var текст = "Не сохранено: " + string.Join("; ", проблемы.Select(п =>
                (п.Name.Length > 0 ? $"«{п.Name}» — " : string.Empty) + п.Reason + (п.Suggestion is { } с ? $" (возможно, {с})" : string.Empty))) + ".";

            throw new AppException(
                ErrorCodes.Validation,
                текст,
                HttpStatusCode.BadRequest,
                new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["field"] = Field,
                    ["problems"] = проблемы.Select(п => new Dictionary<string, object?>(StringComparer.Ordinal)
                    {
                        ["name"] = п.Name,
                        ["reason"] = п.Reason,
                        ["suggestion"] = п.Suggestion,
                    }).ToList(),
                });
        }

        return (поУмолчанию, свои);
    }

    /// <summary>Самое похожее имя из KMP — для подсказки «возможно, вы имели в виду».</summary>
    internal static string? Ближайшее(string имя, IEnumerable<string> известные)
    {
        var искомое = имя.Trim().ToLowerInvariant();
        string? лучшее = null;
        var расстояние = int.MaxValue;

        foreach (var кандидат in известные)
        {
            var д = Левенштейн(искомое, кандидат.ToLowerInvariant());

            if (д < расстояние)
            {
                (лучшее, расстояние) = (кандидат, д);
            }
        }

        return лучшее is not null && расстояние <= Math.Max(2, искомое.Length / 3) ? лучшее : null;
    }

    private static int Левенштейн(string а, string б)
    {
        var строка = new int[б.Length + 1];

        for (var j = 0; j <= б.Length; j++)
        {
            строка[j] = j;
        }

        for (var i = 1; i <= а.Length; i++)
        {
            var угол = строка[0];
            строка[0] = i;

            for (var j = 1; j <= б.Length; j++)
            {
                var верх = строка[j];
                строка[j] = Math.Min(Math.Min(строка[j] + 1, строка[j - 1] + 1), угол + (а[i - 1] == б[j - 1] ? 0 : 1));
                угол = верх;
            }
        }

        return строка[б.Length];
    }
}
