using System.Text.Json.Serialization;

namespace Modules.Settings.Api;

/// <summary>Каким полем настройка рисуется на экране.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<SettingType>))]
internal enum SettingType
{
    /// <summary>Флажок.</summary>
    [JsonStringEnumMemberName("boolean")]
    Boolean,

    /// <summary>Целое число.</summary>
    [JsonStringEnumMemberName("integer")]
    Integer,

    /// <summary>Выбор из закрытого списка.</summary>
    [JsonStringEnumMemberName("enum")]
    Enum,

    /// <summary>Строка.</summary>
    [JsonStringEnumMemberName("string")]
    String,
}

/// <summary>Имя вида настройки таким, каким его видит клиент.</summary>
internal static class SettingTypeNames
{
    /// <summary>Проводное имя вида.</summary>
    public static string Wire(this SettingType type) => type switch
    {
        SettingType.Boolean => "boolean",
        SettingType.Integer => "integer",
        SettingType.Enum => "enum",
        SettingType.String => "string",
        _ => throw new ArgumentOutOfRangeException(nameof(type), type, "Неизвестный вид настройки"),
    };
}
