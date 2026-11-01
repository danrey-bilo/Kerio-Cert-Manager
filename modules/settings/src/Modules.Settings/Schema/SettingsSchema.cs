using Modules.Settings.Storage;
using Modules.Core.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Modules.Settings.Schema;

/// <summary>Контекст схемы модуля — его миграции, снимок и таблица истории (решение 0114).</summary>
internal sealed class SettingsSchema(DbContextOptions<SettingsSchema> options) : ModuleSchemaContext(options, [new SettingsEntities()])
{
    /// <summary>Имя модуля — оно же в имени таблицы истории.</summary>
    public const string Module = "settings";

    /// <summary>Объявление набора для проекта.</summary>
    public static ModuleSchema Declaration { get; } = new(Module, typeof(SettingsSchema), connection => Create(connection));

    /// <summary>Контекст схемы по строке подключения.</summary>
    public static SettingsSchema Create(string connection) => new(
        new DbContextOptionsBuilder<SettingsSchema>()
            .UseNpgsql(connection, npgsql => npgsql
                .MigrationsAssembly(typeof(SettingsSchema).Assembly)
                .MigrationsHistoryTable(ModuleSchema.HistoryTableOf(Module), ModuleSchema.HistorySchema))
            .Options);
}

/// <summary>Контекст схемы для инструментов EF: <c>dotnet ef migrations add</c> не подключается к базе.</summary>
internal sealed class SettingsSchemaFactory : IDesignTimeDbContextFactory<SettingsSchema>
{
    /// <inheritdoc />
    public SettingsSchema CreateDbContext(string[] args) => SettingsSchema.Create("Host=design-time;Database=design-time");
}
