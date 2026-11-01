using System.Globalization;
using System.Net;
using System.Text.Json;
using Modules.Core.Contracts.Settings;
using Modules.Core.Errors;

using Modules.Settings.Api;
using Modules.Settings.Contracts;

namespace Modules.Settings.Values;

/// <summary>Разбор, проверка и упаковка значения настройки по её объявлению.</summary>
internal static class SettingValues
{
    /// <summary>Проверить присланное значение по объявлению и упаковать в JSON для хранения.</summary>
    public static string Pack(ISettingDeclaration declaration, JsonElement value)
    {
        ArgumentNullException.ThrowIfNull(declaration);

        var разобрано = Parse(declaration, value, out var причина);
        if (причина is { } почему)
        {
            throw Отказ(declaration, почему);
        }

        return JsonSerializer.Serialize(разобрано);
    }

    /// <summary>Прочитать хранимое значение.</summary>
    public static object? Read(ISettingDeclaration declaration, string json) =>
        TryRead(declaration, json, out var значение) ? значение : null;

    /// <summary>Прочитать хранимое значение и сказать, годно ли оно.</summary>
    public static bool TryRead(ISettingDeclaration declaration, string json, out object? value)
    {
        ArgumentNullException.ThrowIfNull(declaration);

        value = null;

        try
        {
            using var документ = JsonDocument.Parse(json);
            var значение = Parse(declaration, документ.RootElement, out var причина);

            if (причина is not null)
            {
                return false;
            }

            value = значение;

            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>Разобрать значение по объявлению.</summary>
    private static object? Parse(ISettingDeclaration declaration, JsonElement value, out Причина? причина)
    {
        причина = null;

        if (value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            if (!declaration.IsNullable)
            {
                причина = Причина.НельзяПусто;
            }

            return null;
        }

        var тип = Nullable.GetUnderlyingType(declaration.ValueType) ?? declaration.ValueType;

        // ⚠ Логическое проверяется ПЕРВЫМ и отдельно.
        if (тип == typeof(bool))
        {
            if (value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            {
                причина = Причина.НеТотТип;
                return null;
            }

            return value.GetBoolean();
        }

        if (тип == typeof(int) || тип == typeof(long))
        {
            if (value.ValueKind != JsonValueKind.Number)
            {
                причина = Причина.НеТотТип;
                return null;
            }

            // ⚠ `10.0` и `1e1` отвергаются, и это правильно.
            if (тип == typeof(int))
            {
                if (!value.TryGetInt32(out var целое))
                {
                    причина = Причина.НеТотТип;
                    return null;
                }

                return ВГраницах(declaration, целое, out причина) ? целое : null;
            }

            if (!value.TryGetInt64(out var длинное))
            {
                причина = Причина.НеТотТип;
                return null;
            }

            return ВГраницах(declaration, длинное, out причина) ? длинное : null;
        }

        if (тип == typeof(string))
        {
            if (value.ValueKind != JsonValueKind.String)
            {
                причина = Причина.НеТотТип;
                return null;
            }

            var строка = value.GetString()!;

            if (declaration.Choices.Count > 0 && !declaration.Choices.Contains(строка, StringComparer.Ordinal))
            {
                причина = Причина.НеИзСписка;
                return null;
            }

            if (declaration.Maximum is null && строка.Length > MaxStringLength)
            {
                причина = Причина.Длинно;
                return null;
            }

            return ВГраницах(declaration, строка.Length, out причина) ? строка : null;
        }

        причина = Причина.НеТотТип;
        return null;
    }

    /// <summary>Число внутри объявленных границ.</summary>
    private static bool ВГраницах(ISettingDeclaration declaration, double число, out Причина? причина)
    {
        причина = null;

        if (declaration.Minimum is { } низ && число < низ)
        {
            причина = Причина.Мало;
            return false;
        }

        if (declaration.Maximum is { } верх && число > верх)
        {
            причина = Причина.Много;
            return false;
        }

        return true;
    }

    private static AppException Отказ(ISettingDeclaration declaration, Причина причина)
    {
        var текст = причина switch
        {
            Причина.НельзяПусто => "Значение обязательно.",
            Причина.НеТотТип => $"Ожидалось значение типа {Имя(declaration)}.",
            Причина.Мало => $"Значение меньше допустимого ({Число(declaration.Minimum)}).",
            Причина.Много => $"Значение больше допустимого ({Число(declaration.Maximum)}).",
            Причина.Длинно => $"Строка длиннее {MaxStringLength} знаков.",
            Причина.НеИзСписка =>
                $"Допустимые значения: {string.Join(", ", declaration.Choices)}.",
            _ => "Значение не подходит.",
        };

        return new AppException(
            SettingsErrorCodes.InvalidSettingValue,
            текст,
            HttpStatusCode.UnprocessableContent,
            new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["key"] = declaration.Name,
                ["type"] = Имя(declaration).Wire(),
                ["nullable"] = declaration.IsNullable,
                ["minimum"] = declaration.Minimum,
                ["maximum"] = причина == Причина.Длинно ? MaxStringLength : declaration.Maximum,
                ["choices"] = declaration.Choices.Count > 0 ? declaration.Choices : null,
            });
    }

    /// <summary>Имя типа для клиента.</summary>
    public static SettingType Имя(ISettingDeclaration declaration)
    {
        ArgumentNullException.ThrowIfNull(declaration);

        var тип = Nullable.GetUnderlyingType(declaration.ValueType) ?? declaration.ValueType;

        if (тип == typeof(bool))
        {
            return SettingType.Boolean;
        }

        if (тип == typeof(int) || тип == typeof(long))
        {
            return SettingType.Integer;
        }

        return declaration.Choices.Count > 0 ? SettingType.Enum : SettingType.String;
    }

    private static string Число(double? значение) =>
        значение?.ToString(CultureInfo.InvariantCulture) ?? "—";

    /// <summary>Длина строки у ключа без объявленного предела.</summary>
    internal const int MaxStringLength = 2048;

    private enum Причина
    {
        НельзяПусто,
        НеТотТип,
        Мало,
        Много,
        Длинно,
        НеИзСписка,
    }
}
