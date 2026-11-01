using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Modules.Core.Data;

/// <summary>Набор миграций модуля — его объявление для проекта (решение 0114).</summary>
public sealed record ModuleSchema(string Module, Type Context, Func<string, DbContext> Create)
{
    /// <summary>Таблица истории миграций модуля.</summary>
    public string HistoryTable => HistoryTableOf(Module);

    /// <summary>Схема таблицы истории — названа явно, а не по пути поиска подключения.</summary>
    public const string HistorySchema = "public";

    /// <summary>Применить набор к базе — так поднимают схему проект и тесты модуля.</summary>
    public async Task MigrateAsync(string connection, CancellationToken cancellationToken = default)
    {
        await using var db = Create(connection);

        await db.Database.MigrateAsync(cancellationToken);
    }

    /// <summary>Таблица истории модуля по его имени.</summary>
    public static string HistoryTableOf(string module)
    {
        ArgumentException.ThrowIfNullOrEmpty(module);

        return "__ef_history_" + module.Replace('-', '_');
    }
}

/// <summary>Объявить набор миграций модуля.</summary>
public static class ModuleSchemaExtensions
{
    /// <summary>Объявить набор; проект найдёт его среди служб.</summary>
    public static IServiceCollection AddModuleSchema(this IServiceCollection services, ModuleSchema schema)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(schema);

        services.AddSingleton(schema);

        return services;
    }

    /// <summary>Применить наборы всех подключённых модулей — в порядке подключения.</summary>
    public static async Task MigrateModuleSchemasAsync(
        this IServiceProvider services, string connection, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(services);

        foreach (var schema in services.GetServices<ModuleSchema>())
        {
            await schema.MigrateAsync(connection, cancellationToken);
        }
    }
}
