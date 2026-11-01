using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Unicode;
using Modules.Core.Redaction;

namespace Modules.Audit.Writing;

/// <summary>Заменяет значения секретных полей на звёздочки перед записью в журнал.</summary>
internal sealed class ChangesRedactor
{
    /// <summary>Кодировщик, не превращающий кириллицу в экранированные последовательности.</summary>
    private static readonly JsonSerializerOptions Options = new()
    {
        Encoder = JavaScriptEncoder.Create(UnicodeRanges.All),
    };

    private readonly HashSet<string> _keys;

    public ChangesRedactor(IEnumerable<ISensitiveKeyProvider> providers)
    {
        _keys = new HashSet<string>(SensitiveKeys.Base, StringComparer.OrdinalIgnoreCase);

        foreach (var key in providers.SelectMany(provider => provider.Keys))
        {
            _keys.Add(key);
        }
    }

    /// <summary>Отредактировать и превратить в JSON для колонки.</summary>
    public string? ToJson(IReadOnlyDictionary<string, object?>? changes)
    {
        if (changes is null || changes.Count == 0)
        {
            return null;
        }

        var node = new JsonObject();

        foreach (var (key, value) in changes)
        {
            node[key] = Redact(key, value);
        }

        return node.ToJsonString(Options);
    }

    /// <summary>Рекурсивная замена значения.</summary>
    private JsonNode? Redact(string key, object? value)
    {
        // ⚠ Булево значение не редактируется никогда, каким бы ни было имя.
        if (value is bool)
        {
            return JsonValue.Create((bool)value);
        }

        // ⚠ Не точное совпадение имени.
        if (SensitiveKeys.IsSensitive(key, _keys))
        {
            return JsonValue.Create(SensitiveKeys.Redacted);
        }

        return value switch
        {
            null => null,

            IReadOnlyDictionary<string, object?> map => Map(map),

            IEnumerable<object?> list => Array(key, list),

            JsonElement element => FromElement(key, element),
            JsonNode node => FromNode(key, node),

            bool flag => JsonValue.Create(flag),
            int number => JsonValue.Create(number),
            long number => JsonValue.Create(number),
            double number => JsonValue.Create(number),
            decimal number => JsonValue.Create(number),

            _ => JsonValue.Create(value.ToString()),
        };
    }

    private JsonObject Map(IReadOnlyDictionary<string, object?> map)
    {
        var node = new JsonObject();

        foreach (var (key, value) in map)
        {
            node[key] = Redact(key, value);
        }

        return node;
    }

    private JsonArray Array(string key, IEnumerable<object?> items)
    {
        var node = new JsonArray();

        foreach (var item in items)
        {
            node.Add(Redact(key, item));
        }

        return node;
    }

    private JsonNode? FromElement(string key, JsonElement element) =>
        FromNode(key, JsonNode.Parse(element.GetRawText()));

    private JsonNode? FromNode(string key, JsonNode? node) => node switch
    {
        null => null,
        JsonObject obj => Map(obj.ToDictionary(pair => pair.Key, pair => (object?)pair.Value)),
        JsonArray array => Array(key, array.Select(item => (object?)item)),
        _ => node.DeepClone(),
    };
}
