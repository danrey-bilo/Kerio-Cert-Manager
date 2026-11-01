namespace Modules.Core.Contracts.Settings;

/// <summary>Где живёт значение настройки: одно на установку или своё у каждого человека.</summary>
public enum SettingScope
{
    /// <summary>Одно значение на установку.</summary>
    System,

    /// <summary>Своё у каждого человека.</summary>
    User,
}

/// <summary>Объявление настройки без её типа — то, что нужно экрану и проверке значения.</summary>
public interface ISettingDeclaration
{
    /// <summary>Имя вида <c>audit.retention_months</c>.</summary>
    string Name { get; }

    /// <summary>Название для экрана настроек.</summary>
    string Title { get; }

    /// <summary>Пояснение для экрана настроек.</summary>
    string Description { get; }

    /// <summary>Раздел: обычно имя модуля.</summary>
    string Category { get; }

    /// <summary>Правка меняет поведение защиты.</summary>
    bool IsSecurity { get; }

    /// <summary>Одно на установку или своё у каждого.</summary>
    SettingScope Scope { get; }

    /// <summary>Тип значения — <c>typeof(T)</c>, включая обнуляемость числовых.</summary>
    Type ValueType { get; }

    /// <summary>Что действует, пока не задано.</summary>
    object? DefaultValue { get; }

    /// <summary>Нижняя граница числа или длины строки.</summary>
    double? Minimum { get; }

    /// <summary>Верхняя граница.</summary>
    double? Maximum { get; }

    /// <summary>Допустимые значения.</summary>
    IReadOnlyList<string> Choices { get; }

    /// <summary>Принимает ли настройка <c>null</c>.</summary>
    bool IsNullable { get; }

    /// <summary>Правка действует только после перезапуска — экран обязан это сказать.</summary>
    bool RequiresRestart { get; }
}

/// <summary>Объявление настройки: имя, значение по умолчанию и то, что о ней надо знать человеку.</summary>
public sealed record SettingKey<T>(
    string Name,
    T Default,
    string Title,
    string Description,
    string Category,
    bool IsSecurity = false,
    SettingScope Scope = SettingScope.System,
    double? Minimum = null,
    double? Maximum = null,
    IReadOnlyList<string>? Choices = null,
    bool? Nullable = null,
    bool RequiresRestart = false) : ISettingDeclaration
{
    /// <summary>Допустимые значения — своей копией, а не переданным массивом.</summary>
    public IReadOnlyList<string> Choices { get; } = Choices is null ? [] : [.. Choices];

    /// <inheritdoc />
    public Type ValueType => typeof(T);

    /// <inheritdoc />
    public object? DefaultValue => Default;

    /// <summary>Принимает ли настройка <c>null</c>.</summary>
    public bool IsNullable { get; } =
        Nullable ?? (System.Nullable.GetUnderlyingType(typeof(T)) is not null || Default is null);
}

/// <summary>Чтение действующего значения настройки.</summary>
public interface ISettingsReader
{
    /// <summary>Значение настройки или её умолчание.</summary>
    ValueTask<T> GetAsync<T>(SettingKey<T> key, CancellationToken cancellationToken = default);
}

/// <summary>Модуль объявляет свои настройки.</summary>
public interface ISettingDeclarationProvider
{
    /// <summary>Настройки этого модуля — для экрана и для проверки допустимых значений.</summary>
    IReadOnlyList<ISettingDeclaration> Declarations { get; }
}
