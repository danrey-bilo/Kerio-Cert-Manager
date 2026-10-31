using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace KerioCertManager.Modules.Kerio.Localization;

/// <summary>Тексты сервера на языке читателя: ошибки API, шаги установки, пояснения состояний, письма.</summary>
public sealed class ServerTexts
{
    /// <summary>Язык исходных текстов.</summary>
    public const string Source = "ru";

    /// <summary>Длиннее — не текст сервера, а данные: не переводится.</summary>
    public const int МаксДлина = 2000;

    private static readonly Regex Вставка = new(@"\{(\d+)\}", RegexOptions.CultureInvariant);

    /// <summary>Предложения: после точки, восклицания или вопроса и пробела — новое с заглавной или кавычки.</summary>
    private static readonly Regex Предложения = new(@"(?<=[.!?])\s+(?=[«""(A-ZА-ЯЁІЇЄҐ])", RegexOptions.CultureInvariant);

    private readonly Dictionary<string, Словарь> _словари;

    public ServerTexts(IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> dictionaries)
    {
        ArgumentNullException.ThrowIfNull(dictionaries);
        _словари = dictionaries.ToDictionary(п => п.Key, п => new Словарь(п.Value), StringComparer.Ordinal);
    }

    /// <summary>Словари из сборки: <c>Localization/en.json</c>, <c>Localization/uk.json</c>.</summary>
    public static ServerTexts FromAssembly()
    {
        var сборка = typeof(ServerTexts).Assembly;
        var словари = new Dictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.Ordinal);

        foreach (var язык in Languages.Where(я => я != Source))
        {
            using var поток = сборка.GetManifestResourceStream($"KerioCertManager.Modules.Kerio.Localization.{язык}.json")
                ?? throw new InvalidOperationException($"Нет словаря {язык}.json в сборке.");
            словари[язык] = JsonSerializer.Deserialize<Dictionary<string, string>>(поток) ?? [];
        }

        return new ServerTexts(словари);
    }

    /// <summary>Языки интерфейса и писем.</summary>
    public static IReadOnlyList<string> Languages { get; } = [Source, "en", "uk"];

    /// <summary>Язык из заголовка <c>Accept-Language</c>: первый из известных; нет — исходный.</summary>
    public static string FromAcceptLanguage(string? header)
    {
        if (string.IsNullOrWhiteSpace(header))
        {
            return Source;
        }

        foreach (var часть in header.Split(','))
        {
            var тег = часть.Split(';')[0].Trim();
            var код = (тег.Length >= 2 ? тег[..2] : тег).ToLowerInvariant();

            if (Languages.Contains(код))
            {
                return код;
            }
        }

        return Source;
    }

    /// <summary>Есть ли кириллица (блок U+0400–U+04FF).</summary>
    private static bool Кириллица(string text) => text.AsSpan().ContainsAnyInRange('Ѐ', 'ӿ');

    /// <summary>Нужен ли перевод вообще: язык не исходный и словарь к нему есть.</summary>
    public bool Translates(string language) => _словари.ContainsKey(language);

    /// <summary>Текст на языке <paramref name="language"/>; неизвестный язык и текст без кириллицы — как есть.</summary>
    public string Translate(string text, string language)
    {
        ArgumentNullException.ThrowIfNull(text);

        return text.Length <= МаксДлина && _словари.TryGetValue(language, out var словарь) && Кириллица(text)
            ? словарь.Перевести(text, 0)
            : text;
    }

    /// <summary>Шаблон: постоянные куски между вставками и перевод с теми же номерами.</summary>
    private sealed record Шаблон(string[] Куски, int[] Номера, string Перевод, int Постоянного)
    {
        /// <summary>Вставки по порядку или <c>null</c>: начало и конец — точно, середина — первым вхождением слева.</summary>
        public string[]? Сверить(string text)
        {
            var начало = Куски[0];
            var конец = Куски[^1];

            if (text.Length < начало.Length + конец.Length
                || !text.StartsWith(начало, StringComparison.Ordinal)
                || !text.EndsWith(конец, StringComparison.Ordinal))
            {
                return null;
            }

            var вставки = new string[Куски.Length - 1];
            var позиция = начало.Length;
            var предел = text.Length - конец.Length;

            for (var i = 1; i < Куски.Length - 1; i++)
            {
                var где = text.IndexOf(Куски[i], позиция, предел - позиция, StringComparison.Ordinal);

                if (где < 0)
                {
                    return null;
                }

                вставки[i - 1] = text[позиция..где];
                позиция = где + Куски[i].Length;
            }

            вставки[^1] = text[позиция..предел];
            return вставки;
        }
    }

    private sealed class Словарь
    {
        private const int ГлубинаВставок = 4;

        /// <summary>Потолок памяти переводов — в символах.</summary>
        private const long ПотолокПамяти = 4_000_000;

        private readonly Dictionary<string, string> _точные = new(StringComparer.Ordinal);

        /// <summary>По первому символу; внутри — от самого точного шаблона к общему.</summary>
        private readonly Dictionary<char, Шаблон[]> _поНачалу;

        /// <summary>Шаблоны, что начинаются со вставки, — от точного к общему.</summary>
        private readonly Шаблон[] _сВставки;

        private readonly ConcurrentDictionary<string, string> _память = new(StringComparer.Ordinal);

        private long _символов;

        public Словарь(IReadOnlyDictionary<string, string> записи)
        {
            var шаблоны = new List<Шаблон>();

            // Края текста при сверке обрезаются — так и ключи: « Прежние версии ({0})…» найдётся внутри другой фразы.
            foreach (var (сКраями, перевод) in записи.Select(з => (з.Key, з.Value.Trim())))
            {
                var исходный = сКраями.Trim();

                if (string.IsNullOrEmpty(перевод) || исходный.Length == 0)
                {
                    continue;
                }

                if (!Вставка.IsMatch(исходный))
                {
                    _точные.TryAdd(исходный, перевод);
                    continue;
                }

                var части = Вставка.Split(исходный);
                var куски = части.Where((_, i) => i % 2 == 0).ToArray();
                var номера = части.Where((_, i) => i % 2 == 1).Select(н => int.Parse(н, CultureInfo.InvariantCulture)).ToArray();
                var постоянного = куски.Sum(к => к.Trim().Length);

                if (постоянного >= 2)
                {
                    шаблоны.Add(new Шаблон(куски, номера, перевод, постоянного));
                }
            }

            var поТочности = шаблоны.OrderByDescending(ш => ш.Постоянного).ToList();
            _сВставки = [.. поТочности.Where(ш => ш.Куски[0].Length == 0)];
            _поНачалу = поТочности.Where(ш => ш.Куски[0].Length > 0)
                .GroupBy(ш => ш.Куски[0][0])
                .ToDictionary(г => г.Key, г => г.ToArray());
        }

        public string Перевести(string text, int глубина)
        {
            if (глубина > ГлубинаВставок || !Кириллица(text))
            {
                return text;
            }

            if (_точные.TryGetValue(text, out var точный))
            {
                return точный;
            }

            if (_память.TryGetValue(text, out var был))
            {
                return был;
            }

            var ядро = text.Trim();
            string итог;

            if (ядро.Length != text.Length)
            {
                var слева = text[..text.IndexOf(ядро, StringComparison.Ordinal)];
                итог = слева + Перевести(ядро, глубина) + text[(слева.Length + ядро.Length)..];
            }
            else
            {
                итог = ПоШаблону(text, глубина) ?? ПоПредложениям(text, глубина) ?? text;
            }

            if (Interlocked.Add(ref _символов, text.Length + итог.Length) > ПотолокПамяти)
            {
                _память.Clear();
                Interlocked.Exchange(ref _символов, 0);
            }

            _память[text] = итог;

            return итог;
        }

        private string? ПоШаблону(string text, int глубина)
        {
            Шаблон? лучший = null;
            string[]? вставки = null;

            if (_поНачалу.TryGetValue(text[0], out var свои))
            {
                foreach (var шаблон in свои)
                {
                    if (шаблон.Сверить(text) is { } найдены)
                    {
                        (лучший, вставки) = (шаблон, найдены);
                        break;
                    }
                }
            }

            foreach (var шаблон in _сВставки)
            {
                if (лучший is not null && шаблон.Постоянного <= лучший.Постоянного)
                {
                    break;
                }

                if (шаблон.Сверить(text) is { } найдены)
                {
                    (лучший, вставки) = (шаблон, найдены);
                    break;
                }
            }

            if (лучший is null || вставки is null)
            {
                return null;
            }

            var значения = new Dictionary<int, string>();

            for (var i = 0; i < лучший.Номера.Length; i++)
            {
                значения[лучший.Номера[i]] = Перевести(вставки[i], глубина + 1);
            }

            return Вставка.Replace(
                лучший.Перевод,
                m => значения.TryGetValue(int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture), out var значение) ? значение : m.Value);
        }

        private string? ПоПредложениям(string text, int глубина)
        {
            var части = Предложения.Split(text);

            if (части.Length < 2)
            {
                return null;
            }

            var переведены = части.Select(ч => Перевести(ч, глубина + 1)).ToList();

            return переведены.SequenceEqual(части) ? null : string.Join(' ', переведены);
        }
    }
}
