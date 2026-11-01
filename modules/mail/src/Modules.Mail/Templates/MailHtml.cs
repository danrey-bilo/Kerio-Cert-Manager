using System.Net;
using System.Text;
using Modules.Core.Contracts.Mail;

namespace Modules.Mail.Templates;

/// <summary>HTML-часть письма: тот же текст шаблона абзацами, таблицы — таблицами.</summary>
internal static class MailHtml
{
    /// <summary>Контекст AEAD HTML-части письма в очереди.</summary>
    internal const string Контекст = "eams:mail-html";

    /// <summary>Шрифт письма: системный, затем Segoe UI и Arial.</summary>
    private const string Шрифт = "system-ui,-apple-system,'Segoe UI',Roboto,Arial,sans-serif";

    /// <summary>Отступ абзаца и таблицы.</summary>
    private const string Отступ = "margin:0 0 12px 0;";

    /// <summary>Ячейка: тонкая рамка и поля.</summary>
    private const string СтильЯчейки = "border:1px solid #d0d0d0;padding:6px 10px;text-align:left;vertical-align:top;";

    /// <summary>Заголовок колонки: ячейка на бледном фоне.</summary>
    private const string СтильЗаголовка = СтильЯчейки + "background-color:#f2f2f2;font-weight:600;";

    /// <summary>Собрать HTML-документ письма.</summary>
    public static string Собрать(
        string subject, IReadOnlyList<MailBodyPart> parts, string locale, bool frame, string product)
    {
        ArgumentNullException.ThrowIfNull(subject);
        ArgumentNullException.ThrowIfNull(parts);
        ArgumentNullException.ThrowIfNull(product);

        var html = new StringBuilder();

        html.Append("<!DOCTYPE html>\n<html lang=\"").Append(Код(locale)).Append("\">\n<head>\n")
            .Append("<meta charset=\"utf-8\">\n")
            .Append("<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">\n")
            .Append("<title>").Append(Код(subject)).Append("</title>\n</head>\n")
            .Append("<body style=\"margin:0;padding:0;background-color:#ffffff;\">\n")
            .Append("<div style=\"padding:16px;font-family:").Append(Шрифт)
            .Append(";font-size:14px;line-height:1.5;color:#1f1f1f;\">\n");

        // ⚠ Рамка — те же слова, что у текстовой части (`MailFrame.Слова`), и только с параметром `Frame`.
        var (привет, спасибо) = MailFrame.Слова(locale);

        if (frame)
        {
            Абзац(html, [привет]);
        }

        foreach (var часть in parts)
        {
            if (часть.Table is { } таблица)
            {
                Таблица(html, таблица);
            }
            else
            {
                Абзацы(html, часть.Text ?? string.Empty);
            }
        }

        if (frame)
        {
            Абзац(html, [спасибо, product]);
        }

        return html.Append("</div>\n</body>\n</html>\n").ToString();
    }

    /// <summary>Текст абзацами: пустая строка разделяет абзацы, перевод строки внутри абзаца — <c>&lt;br&gt;</c>.</summary>
    private static void Абзацы(StringBuilder html, string текст)
    {
        var строки = new List<string>();

        foreach (var строка in текст.Split('\n'))
        {
            if (string.IsNullOrWhiteSpace(строка))
            {
                Абзац(html, строки);
                строки.Clear();
            }
            else
            {
                строки.Add(строка.TrimEnd('\r'));
            }
        }

        Абзац(html, строки);
    }

    /// <summary>Один абзац; пустой не пишется.</summary>
    private static void Абзац(StringBuilder html, IReadOnlyList<string> строки)
    {
        if (строки.Count == 0)
        {
            return;
        }

        html.Append("<p style=\"").Append(Отступ).Append("\">")
            .AppendJoin("<br>\n", строки.Select(Код))
            .Append("</p>\n");
    }

    /// <summary>Таблица: строка заголовков и строка на ряд; ячейки — одной строкой и экранированы.</summary>
    private static void Таблица(StringBuilder html, MailTable таблица)
    {
        html.Append("<table cellpadding=\"0\" cellspacing=\"0\" border=\"0\" style=\"border-collapse:collapse;")
            .Append(Отступ).Append("font-size:13px;\">\n<thead>\n<tr>");

        foreach (var колонка in таблица.Columns)
        {
            html.Append("<th style=\"").Append(СтильЗаголовка).Append("\">").Append(Код(MailTemplates.Ячейка(колонка))).Append("</th>");
        }

        html.Append("</tr>\n</thead>\n<tbody>\n");

        foreach (var строка in таблица.Rows)
        {
            html.Append("<tr>");

            foreach (var ячейка in строка)
            {
                html.Append("<td style=\"").Append(СтильЯчейки).Append("\">").Append(Код(MailTemplates.Ячейка(ячейка))).Append("</td>");
            }

            html.Append("</tr>\n");
        }

        html.Append("</tbody>\n</table>\n");
    }

    /// <summary>Текст для разметки: <c>&lt; &gt; &amp; " '</c> — сущностями.</summary>
    private static string Код(string текст) => WebUtility.HtmlEncode(текст);
}
