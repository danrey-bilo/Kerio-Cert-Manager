using System.Text.Json;

namespace Modules.Mail.Secrets;

/// <summary>Секрет вместе с адресом, для которого его ввели.</summary>
internal sealed class BoundSecret(string target, string secret)
{
    /// <summary>Версия формата.</summary>
    private const int Версия = 1;

    /// <summary>Адрес, для которого секрет ввели, в нормальном виде владельца секрета.</summary>
    public string Target { get; } = target ?? throw new ArgumentNullException(nameof(target));

    /// <summary>Сам секрет.</summary>
    public string Secret { get; } = secret ?? throw new ArgumentNullException(nameof(secret));

    /// <summary>Введён ли секрет для этого адреса.</summary>
    public bool IsFor(string target) => string.Equals(Target, target, StringComparison.Ordinal);

    /// <summary>Текст для запечатывания.</summary>
    public string Pack() => JsonSerializer.Serialize(new Dictionary<string, object>(StringComparer.Ordinal)
    {
        ["v"] = Версия,
        ["target"] = Target,
        ["secret"] = Secret,
    });

    /// <summary>Разобрать открытый текст; <c>null</c> — старый формат, голый секрет без привязки.</summary>
    public static BoundSecret? Unpack(string opened)
    {
        ArgumentNullException.ThrowIfNull(opened);

        if (!opened.StartsWith('{'))
        {
            return null;
        }

        try
        {
            using var документ = JsonDocument.Parse(opened);
            var корень = документ.RootElement;

            return корень.ValueKind == JsonValueKind.Object
                && корень.TryGetProperty("v", out var версия)
                && версия.ValueKind == JsonValueKind.Number
                && версия.TryGetInt32(out var номер)
                && номер == Версия
                && корень.TryGetProperty("target", out var адрес)
                && адрес.ValueKind == JsonValueKind.String
                && корень.TryGetProperty("secret", out var значение)
                && значение.ValueKind == JsonValueKind.String
                    ? new BoundSecret(адрес.GetString()!, значение.GetString()!)
                    : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Сам секрет не печатается никогда.</summary>
    public override string ToString() => "<секрет, привязанный к адресу>";
}
