using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Modules.Core.Data;

namespace KerioCertManager.Api.Schema;

/// <summary>Набор миграций проекта: значения поверх таблиц модулей.</summary>
internal sealed class KerioCertManagerSchema(DbContextOptions<KerioCertManagerSchema> options) : ModuleSchemaContext(options, [])
{
    /// <summary>Имя набора — оно же в имени таблицы истории.</summary>
    public const string Module = "kcm";

    public static ModuleSchema Declaration { get; } = new(Module, typeof(KerioCertManagerSchema), connection => Create(connection));

    /// <summary>Контекст набора по строке подключения.</summary>
    public static KerioCertManagerSchema Create(string connection) => new(
        new DbContextOptionsBuilder<KerioCertManagerSchema>()
            .UseNpgsql(connection, npgsql => npgsql
                .MigrationsAssembly(typeof(KerioCertManagerSchema).Assembly)
                .MigrationsHistoryTable(ModuleSchema.HistoryTableOf(Module), ModuleSchema.HistorySchema))
            .Options);
}

/// <summary>Контекст набора проекта для инструментов EF.</summary>
internal sealed class KerioCertManagerSchemaFactory : IDesignTimeDbContextFactory<KerioCertManagerSchema>
{
    /// <inheritdoc />
    public KerioCertManagerSchema CreateDbContext(string[] args) => KerioCertManagerSchema.Create("Host=design-time;Database=design-time");
}
