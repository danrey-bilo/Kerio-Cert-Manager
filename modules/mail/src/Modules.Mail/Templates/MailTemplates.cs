using System.Globalization;
using System.Text;
using Modules.Core.Contracts.Mail;

namespace Modules.Mail.Templates;

/// <summary>Всё, что объявили модули: пара «вид + язык» → текст.</summary>
internal sealed class MailTemplates
{
    private readonly Dictionary<(string Kind, string Locale), MailTemplate> _byKey;

    /// <summary>Виды, с которых их модули сняли предел частоты.</summary>
    private readonly HashSet<string> _безПредела;

    /// <summary>Оборачивать ли тела рамкой (решение 0120, <c>MailModuleOptions.Frame</c>).</summary>
    private readonly bool _рамка;

    /// <summary>Собирать ли к тексту HTML-часть (решение 0121, <c>MailModuleOptions.Html</c>).</summary>
    private readonly bool _html;

    public MailTemplates(IEnumerable<IMailTemplateProvider> providers, MailModuleOptions? options = null)
    {
        _продукт = options?.ProductName ?? MailModuleOptions.DefaultProductName;
        _рамка = options?.Frame ?? false;
        _html = options?.Html ?? false;

        ArgumentNullException.ThrowIfNull(providers);

        // ⚠ Список, а не перечисление: поставщиков обходят дважды — тексты и виды без предела.
        var поставщики = providers.ToList();

        _byKey = [];
        var споры = new List<string>();

        foreach (var текст in поставщики.SelectMany(п => п.Templates))
        {
            if (!MailLocales.All.Contains(текст.Locale, StringComparer.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Письмо «{текст.Kind}» объявлено на языке «{текст.Locale}», которого нет в "
                    + $"списке: {string.Join(", ", MailLocales.All)}. Язык, которого нет в "
                    + "настройке `ui.locale`, выбрать нельзя, и письмо на нём не придёт никому.");
            }

            if (!_byKey.TryAdd((текст.Kind, текст.Locale), текст))
            {
                споры.Add($"{текст.Kind}/{текст.Locale}");
            }
        }

        if (споры.Count > 0)
        {
            throw new InvalidOperationException(
                "Два модуля объявили одно и то же письмо: "
                + string.Join(", ", споры.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal))
                + ". Какой из текстов уйдёт человеку, решал бы порядок регистрации модулей.");
        }

        // ⚠ Полнота проверяется ПОСЛЕ сбора всех: вид может объявляться по частям — скажем, общий текст модулем, а перевод дополнением.
        var неполные = _byKey.Keys
            .Select(ключ => ключ.Kind)
            .Distinct(StringComparer.Ordinal)
            .Where(вид => MailLocales.All.Any(язык => !_byKey.ContainsKey((вид, язык))))
            .Order(StringComparer.Ordinal)
            .ToList();

        if (неполные.Count > 0)
        {
            var подробности = неполные.Select(вид =>
                $"{вид} (нет: {string.Join(", ", MailLocales.All.Where(я => !_byKey.ContainsKey((вид, я))))})");

            throw new InvalidOperationException(
                "Письмо объявлено не на всех языках: " + string.Join("; ", подробности)
                + ". Неполный вид означает письмо, которое одному человеку придёт, а другому "
                + "нет — и обнаружится это у второго.");
        }

        Kinds = [.. _byKey.Keys.Select(к => к.Kind).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)];

        // ⚠ Вид без предела обязан быть объявлен шаблонами — проверка после сбора всех текстов, по той же причине, что полнота языков.
        _безПредела = new HashSet<string>(поставщики.SelectMany(п => п.UnthrottledKinds), StringComparer.Ordinal);

        var незнакомые = _безПредела.Where(вид => !Knows(вид)).Order(StringComparer.Ordinal).ToList();

        if (незнакомые.Count > 0)
        {
            throw new InvalidOperationException(
                "Предел частоты снят с видов писем, которых не объявил ни один модуль: "
                + string.Join(", ", незнакомые) + ". Снятие защиты, которое молча не состоялось, так же "
                + "плохо, как снятое молча.");
        }
    }

    /// <summary>Все объявленные виды писем.</summary>
    public IReadOnlyList<string> Kinds { get; }

    /// <summary>Есть ли такой вид.</summary>
    public bool Knows(string kind) => Kinds.Contains(kind, StringComparer.Ordinal);

    /// <summary>Действует ли на вид предел <c>mail.rate_per_hour</c> (решение 0120).</summary>
    public bool IsThrottled(string kind) => !_безПредела.Contains(kind);

    /// <summary>Отрисовать письмо: тема и тело с подставленными значениями.</summary>
    public (string Subject, string Body) Render(
        string kind, string locale, IReadOnlyDictionary<string, string> values)
    {
        var письмо = Отрисовать(kind, locale, values, БезТаблиц);

        return (письмо.Subject, письмо.Body);
    }

    /// <summary>Письмо без таблиц.</summary>
    internal static readonly IReadOnlyDictionary<string, MailTable> БезТаблиц =
        new Dictionary<string, MailTable>(StringComparer.Ordinal);

    /// <summary>Отрисовать письмо с таблицами: тема, тело текстом и — с параметром <c>Html</c> — тело в HTML.</summary>
    public MailRendering Отрисовать(
        string kind,
        string locale,
        IReadOnlyDictionary<string, string> values,
        IReadOnlyDictionary<string, MailTable> tables)
    {
        ArgumentNullException.ThrowIfNull(values);
        ArgumentNullException.ThrowIfNull(tables);

        var текст = _byKey.GetValueOrDefault((kind, locale))
            ?? _byKey.GetValueOrDefault((kind, MailLocales.En))
            ?? throw new InvalidOperationException(
                $"Письма «{kind}» не объявлял ни один модуль. Вид письма — не строка в вызове, "
                + "а объявление в `IMailTemplateProvider`.");

        // ⚠ Имя продукта — значение `{product}` (решение 0111): письма объявляют модули, а называть продукт вправе только проект.
        var всё = values.ContainsKey(ProductToken)
            ? values
            : new Dictionary<string, string>(values, StringComparer.Ordinal) { [ProductToken] = _продукт };

        var части = Части(текст.Body, tables, kind)
            .Select(часть => часть.Table is null ? часть with { Text = Подставить(часть.Text!, всё) } : часть)
            .ToList();

        var тело = string.Join('\n', части.Select(часть => часть.Table is { } таблица ? Текстом(таблица) : часть.Text));
        var тема = Подставить(текст.Subject, всё);

        // ⚠ Рамка — только с параметром (решение 0120): без него тело байт в байт прежнее.
        return new MailRendering(
            тема,
            _рамка ? MailFrame.Wrap(тело, текст.Locale, _продукт) : тело,
            _html ? MailHtml.Собрать(тема, части, текст.Locale, _рамка, _продукт) : null);
    }

    /// <summary>Разбить тело шаблона на текст и таблицы по строкам-заполнителям переданных таблиц.</summary>
    private static List<MailBodyPart> Части(string body, IReadOnlyDictionary<string, MailTable> tables, string kind)
    {
        if (tables.Count == 0)
        {
            return [new MailBodyPart(body, null)];
        }

        var части = new List<MailBodyPart>();
        var строки = new List<string>();
        var встречены = new HashSet<string>(StringComparer.Ordinal);

        foreach (var строка in body.Split('\n'))
        {
            if (Заполнитель(строка) is { } имя && tables.TryGetValue(имя, out var таблица))
            {
                if (строки.Count > 0)
                {
                    части.Add(new MailBodyPart(string.Join('\n', строки), null));
                    строки.Clear();
                }

                части.Add(new MailBodyPart(null, таблица ?? throw new ArgumentException($"Таблица «{имя}» — null.", nameof(tables))));
                встречены.Add(имя);
            }
            else
            {
                строки.Add(строка);
            }
        }

        if (строки.Count > 0)
        {
            части.Add(new MailBodyPart(string.Join('\n', строки), null));
        }

        var лишние = tables.Keys.Where(имя => !встречены.Contains(имя)).Order(StringComparer.Ordinal).ToList();

        if (лишние.Count > 0)
        {
            throw new ArgumentException(
                $"В шаблоне письма «{kind}» нет строки-заполнителя для таблиц: "
                + string.Join(", ", лишние.Select(имя => "{" + ТаблицаТокен + имя + "}"))
                + ". Таблица, молча не попавшая в письмо, хуже отказа.",
                nameof(tables));
        }

        return части;
    }

    /// <summary>Начало заполнителя таблицы: <c>{table:имя}</c>.</summary>
    public const string ТаблицаТокен = "table:";

    /// <summary>Имя таблицы, если строка шаблона — заполнитель и больше ничего; иначе <c>null</c>.</summary>
    private static string? Заполнитель(string строка)
    {
        var т = строка.Trim();

        if (т.Length < ТаблицаТокен.Length + 3
            || !т.StartsWith("{" + ТаблицаТокен, StringComparison.Ordinal)
            || !т.EndsWith('}'))
        {
            return null;
        }

        var имя = т[(ТаблицаТокен.Length + 1)..^1];

        return имя.Length == 0 || имя.Contains('{', StringComparison.Ordinal) || имя.Contains('}', StringComparison.Ordinal)
            ? null
            : имя;
    }

    /// <summary>Таблица текстом: строка заголовков, затем строка на ряд, ячейки через « · ».</summary>
    internal static string Текстом(MailTable таблица) =>
        string.Join(
            '\n',
            new[] { таблица.Columns }.Concat(таблица.Rows).Select(строка => string.Join(РазделительЯчеек, строка.Select(Ячейка))));

    /// <summary>Разделитель ячеек в текстовой части.</summary>
    internal const string РазделительЯчеек = " · ";

    /// <summary>Ячейка таблицы одной строкой: переводы строк и табуляция — пробелом, прочие управляющие знаки и знаки формата выброшены.</summary>
    internal static string Ячейка(string значение)
    {
        var ячейка = new StringBuilder(значение.Length);

        foreach (var руна in значение.EnumerateRunes())
        {
            if (Разрыв(руна))
            {
                if (ячейка.Length > 0 && ячейка[^1] != ' ')
                {
                    ячейка.Append(' ');
                }
            }
            else if (!Лишний(руна))
            {
                ячейка.Append(руна.ToString());
            }
        }

        return ячейка.ToString().Trim();
    }

    private static bool Разрыв(Rune знак) =>
        знак.Value is '\t' or '\n' or '\v' or '\f' or '\r' or 0x85 or 0x2028 or 0x2029;

    /// <summary>Токен имени продукта в шаблонах писем.</summary>
    public const string ProductToken = "product";

    private readonly string _продукт;

    /// <summary>Подставить значения в токены вида <c>{link}</c>.</summary>
    private static string Подставить(string шаблон, IReadOnlyDictionary<string, string> values)
    {
        if (!шаблон.Contains('{', StringComparison.Ordinal))
        {
            return шаблон;
        }

        var итог = new StringBuilder(шаблон.Length);
        var i = 0;

        while (i < шаблон.Length)
        {
            var начало = шаблон.IndexOf('{', i);

            if (начало < 0)
            {
                итог.Append(шаблон, i, шаблон.Length - i);
                break;
            }

            var конец = шаблон.IndexOf('}', начало + 1);

            if (конец < 0)
            {
                итог.Append(шаблон, i, шаблон.Length - i);
                break;
            }

            итог.Append(шаблон, i, начало - i);

            var имя = шаблон[(начало + 1)..конец];

            итог.Append(values.TryGetValue(имя, out var значение)
                ? Чистое(значение)
                : шаблон[начало..(конец + 1)]);

            i = конец + 1;
        }

        return итог.ToString();
    }

    /// <summary>Значение без управляющих и невидимых знаков.</summary>
    internal static string Чистое(string значение)
    {
        if (!значение.EnumerateRunes().Any(Лишний))
        {
            return значение;
        }

        var чистое = new StringBuilder(значение.Length);

        foreach (var руна in значение.EnumerateRunes())
        {
            if (!Лишний(руна))
            {
                чистое.Append(руна.ToString());
            }
        }

        return чистое.ToString();
    }

    private static bool Лишний(Rune знак) =>
        Rune.IsControl(знак) || Rune.GetUnicodeCategory(знак) == UnicodeCategory.Format;

    /// <summary>Число в текст — единообразно и без зависимости от языка машины.</summary>
    public static string Число(int значение) => значение.ToString(CultureInfo.InvariantCulture);
}

/// <summary>Отрисованное письмо (решение 0121).</summary>
internal sealed record MailRendering(string Subject, string Body, string? Html);

/// <summary>Часть тела письма: текст или таблица — ровно одно из двух.</summary>
internal sealed record MailBodyPart(string? Text, MailTable? Table);
